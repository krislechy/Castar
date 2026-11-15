using Castar.Services.Receipts.Exceptions;
using Castar.Services.Receipts.Models;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Castar.Services.Receipts
{
    public sealed class Nalog : IReceiptProvider, IReceipt<Ticket>, IDisposable
    {
        public const string ProviderId = "nalog";
        private const string ClientSecret = "IyvrAbKt9h/8p6a7QPh8gpkXYQ4=";
        private const string DeviceId = "7C82010F-16CC-558A-8F66-FC4080C66521";

        private readonly Func<NetworkCredential> _getCredential;
        private readonly HttpClient _client;
        private readonly SemaphoreSlim _gate = new(1);
        private NetworkCredential? _sessionCredential;
        private string? _sessionId;
        private string? _refreshToken;

        public Nalog(NetworkCredential credential) : this(() => credential)
        {
        }

        public Nalog(Func<NetworkCredential> getCredential)
            : this(getCredential, new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
        {
        }

        internal Nalog(Func<NetworkCredential> getCredential, HttpMessageHandler handler)
        {
            ArgumentNullException.ThrowIfNull(getCredential);
            _getCredential = getCredential;
            _client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://irkkt-mobile.nalog.ru:8888/v2/"),
                Timeout = TimeSpan.FromSeconds(30),
                MaxResponseContentBufferSize = 4 * 1024 * 1024
            };

            _client.DefaultRequestHeaders.Add("Device-OS", "iOS");
            _client.DefaultRequestHeaders.Add("clientVersion", "2.25.0");
            _client.DefaultRequestHeaders.Add("Device-Id", DeviceId);
            _client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("billchecker/2.9.0 (iPhone; iOS 13.6; Scale/2.00)");
            _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU;q=1, en-US;q=0.9");
        }

        public string Id => ProviderId;

        public bool CanHandle(string query) =>
            !string.IsNullOrWhiteSpace(query) && query.Trim().StartsWith("t=", StringComparison.OrdinalIgnoreCase);

        public Task<Ticket> GetReceipt(string query) => GetTicketAsync(query, CancellationToken.None);

        public async Task<Receipt> GetReceiptAsync(string query, CancellationToken cancellationToken)
        {
            var ticket = await GetTicketAsync(query, cancellationToken);
            var receipt = ticket.ticket?.document?.receipt;
            if (receipt is null || string.IsNullOrWhiteSpace(receipt.user) || receipt.items is not { Count: > 0 })
                throw new InvalidDataException("В ответе ФНС отсутствуют данные для сохранения чека.");

            var identity = !string.IsNullOrWhiteSpace(receipt.fiscalDriveNumber)
                && receipt.fiscalDocumentNumber > 0 && receipt.fiscalSign > 0
                ? FormattableString.Invariant($"{receipt.fiscalDriveNumber}:{receipt.fiscalDocumentNumber}:{receipt.fiscalSign}")
                : ticket.id;

            if (string.IsNullOrWhiteSpace(identity))
                throw new InvalidDataException("В ответе ФНС отсутствует номер чека.");

            var items = new List<ReceiptItem>(receipt.items.Count);
            foreach (var item in receipt.items)
            {
                if (item is null || !double.IsFinite(item.quantity) || item.quantity >= (double)decimal.MaxValue
                    || item.quantity <= (double)decimal.MinValue)
                {
                    throw new InvalidDataException("ФНС вернула товар с неверным количеством.");
                }

                var name = string.IsNullOrWhiteSpace(item.name) ? "Товар без названия" : item.name;
                items.Add(new ReceiptItem(name, (decimal)item.quantity, item.priceDecimal, item.sumDecimal));
            }

            return new Receipt(
                identity, receipt.user, receipt.retailPlace, receipt.retailPlaceAddress,
                DateTimeOffset.FromUnixTimeSeconds(receipt.dateTime), "RUB", receipt.totalSumDecimal, items,
                string.IsNullOrWhiteSpace(ticket.qr) ? query.Trim() : ticket.qr);
        }

        private async Task<Ticket> GetTicketAsync(string query, CancellationToken cancellationToken)
        {
            if (!CanHandle(query))
                throw new ArgumentException("Укажите содержимое QR-кода чека ФНС.", nameof(query));

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var credential = _getCredential();
                if (credential is null || string.IsNullOrWhiteSpace(credential.UserName) || string.IsNullOrWhiteSpace(credential.Password))
                    throw new InvalidOperationException("Укажите ИНН и пароль ФНС в настройках.");

                if (_sessionCredential is null || _sessionCredential.UserName != credential.UserName
                    || _sessionCredential.Password != credential.Password)
                {
                    _sessionCredential = new NetworkCredential(credential.UserName, credential.SecurePassword);
                    _sessionId = null;
                    _refreshToken = null;
                }

                if (_sessionId is null)
                    await AuthenticateAsync(credential, cancellationToken);

                try
                {
                    return await FetchTicketAsync(query.Trim(), cancellationToken);
                }
                catch (HttpRequestException exception) when (IsUnauthorized(exception))
                {
                    await RenewSessionAsync(credential, cancellationToken);
                    return await FetchTicketAsync(query.Trim(), cancellationToken);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task AuthenticateAsync(NetworkCredential credential, CancellationToken cancellationToken)
        {
            var result = await SendAsync<Authenticate>(HttpMethod.Post, "mobile/users/lkfl/auth", new
            {
                inn = credential.UserName,
                password = credential.Password,
                client_secret = ClientSecret
            }, null, cancellationToken);
            SetSession(result.sessionId, result.refresh_token);
        }

        private async Task RenewSessionAsync(NetworkCredential credential, CancellationToken cancellationToken)
        {
            _sessionId = null;
            var refreshToken = _refreshToken;
            _refreshToken = null;

            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                try
                {
                    var result = await SendAsync<RefreshToken>(HttpMethod.Post, "mobile/users/refresh",
                        new { refresh_token = refreshToken, client_secret = ClientSecret }, null, cancellationToken);
                    SetSession(result.sessionId, result.refresh_token);
                    return;
                }
                catch (HttpRequestException exception) when (IsUnauthorized(exception)
                    || exception.StatusCode == HttpStatusCode.BadRequest)
                {
                    // Obtain a new session if the server rejected the refresh token.
                }
            }

            await AuthenticateAsync(credential, cancellationToken);
        }

        private void SetSession(string? sessionId, string? refreshToken)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new InvalidDataException("ФНС не вернула идентификатор сеанса.");

            _sessionId = sessionId;
            _refreshToken = refreshToken;
        }

        private async Task<Ticket> FetchTicketAsync(string query, CancellationToken cancellationToken)
        {
            var registered = await SendAsync<TicketId>(HttpMethod.Post, "ticket", new { qr = query },
                _sessionId, cancellationToken);
            if (string.IsNullOrWhiteSpace(registered.id) || registered.id is "." or "..")
                throw new InvalidDataException("ФНС не вернула идентификатор чека.");

            var result = await SendAsync<Ticket>(HttpMethod.Get, $"tickets/{Uri.EscapeDataString(registered.id)}",
                null, _sessionId, cancellationToken);
            if (result.ticket is null)
                throw new TicketNotReadyException(result.status, "Чек ещё не готов или не существует. Попробуйте позже.");

            result.id ??= registered.id;
            return result;
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string path, object? data, string? sessionId,
            CancellationToken cancellationToken) where T : class
        {
            using var request = new HttpRequestMessage(method, path);
            if (sessionId is not null)
                request.Headers.Add("sessionId", sessionId);
            if (data is not null)
                request.Content = JsonContent.Create(data);

            using var response = await _client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            try
            {
                return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                    ?? throw new InvalidDataException("ФНС вернула пустой ответ.");
            }
            catch (JsonException)
            {
                throw new InvalidDataException("ФНС вернула ответ в неожиданном формате.");
            }
        }

        private static bool IsUnauthorized(HttpRequestException exception) =>
            exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        public void Dispose()
        {
            _client.Dispose();
            _gate.Dispose();
        }
    }
}
