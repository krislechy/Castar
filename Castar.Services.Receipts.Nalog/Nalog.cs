using Castar.Services.Receipts.Exceptions;
using Castar.Services.Receipts.Models;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace Castar.Services.Receipts
{
    public sealed class Nalog : ReceiptProvider
    {
        public const string ProviderId = "nalog";
        private const string ClientSecret = "IyvrAbKt9h/8p6a7QPh8gpkXYQ4=";
        private const string DeviceId = "7C82010F-16CC-558A-8F66-FC4080C66521";

        public sealed record TicketIdentity(string? FiscalDriveNumber, int FiscalDocumentNumber, long FiscalSign,
            string? TicketId) : ReceiptIdentitySource;

        private readonly Func<NetworkCredential> _getCredential;
        private readonly SemaphoreSlim _gate = new(1);
        private NetworkCredential? _sessionCredential;
        private string? _sessionId;
        private string? _refreshToken;

        public Nalog(NetworkCredential credential) : this(() => credential)
        {
        }

        public Nalog(Func<NetworkCredential> getCredential) : this(getCredential, CreateHttpHandler())
        {
        }

        internal Nalog(Func<NetworkCredential> getCredential, HttpMessageHandler handler)
            : base(handler, new Uri("https://irkkt-mobile.nalog.ru:8888/v2/"))
        {
            ArgumentNullException.ThrowIfNull(getCredential);
            _getCredential = getCredential;

            Client.DefaultRequestHeaders.Add("Device-OS", "iOS");
            Client.DefaultRequestHeaders.Add("clientVersion", "2.25.0");
            Client.DefaultRequestHeaders.Add("Device-Id", DeviceId);
            Client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("billchecker/2.9.0 (iPhone; iOS 13.6; Scale/2.00)");
            Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU;q=1, en-US;q=0.9");
        }

        public override string Id => ProviderId;

        public override ReceiptProviderAuthentication Authentication { get; } = new(false, true,
            [new("inn", "ИНН", true, false), new("password", "Пароль", true, true)]);

        public override bool CanHandle(string query) =>
            !string.IsNullOrWhiteSpace(query) && query.Trim().StartsWith("t=", StringComparison.OrdinalIgnoreCase);

        public override string GetReceiptIdentity(ReceiptIdentitySource source)
        {
            if (source is not TicketIdentity ticket)
                throw new ArgumentException("Ожидается идентификатор чека ФНС.", nameof(source));

            if (!string.IsNullOrWhiteSpace(ticket.FiscalDriveNumber)
                && ticket.FiscalDocumentNumber > 0 && ticket.FiscalSign > 0)
            {
                return FormattableString.Invariant($"{ticket.FiscalDriveNumber}:{ticket.FiscalDocumentNumber}:{ticket.FiscalSign}");
            }

            if (string.IsNullOrWhiteSpace(ticket.TicketId))
                throw new InvalidDataException("В ответе ФНС отсутствует номер чека.");

            return ticket.TicketId;
        }

        protected override async Task<ReceiptDetails> FetchReceiptAsync(string query, CancellationToken cancellationToken)
        {
            var ticket = await GetTicketAsync(query, cancellationToken);
            var receipt = ticket.ticket?.document?.receipt;
            if (receipt is null || string.IsNullOrWhiteSpace(receipt.user) || receipt.items is not { Count: > 0 })
                throw new InvalidDataException("В ответе ФНС отсутствуют данные для сохранения чека.");

            var items = new List<ReceiptItem>(receipt.items.Count);
            foreach (var item in receipt.items)
            {
                if (item is null || !double.IsFinite(item.quantity) || item.quantity >= (double)decimal.MaxValue
                    || item.quantity <= (double)decimal.MinValue)
                {
                    throw new InvalidDataException("ФНС вернула товар с неверным количеством.");
                }

                items.Add(new ReceiptItem(NormalizeItemName(item.name), (decimal)item.quantity, item.priceDecimal, item.sumDecimal));
            }

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(receipt.dateTime);
            var verificationCode = string.IsNullOrWhiteSpace(ticket.qr) ? query : ticket.qr;
            return new ReceiptDetails(
                new TicketIdentity(receipt.fiscalDriveNumber, receipt.fiscalDocumentNumber, receipt.fiscalSign, ticket.id),
                receipt.user, receipt.retailPlace, receipt.retailPlaceAddress,
                issuedAt, "RUB", receipt.totalSumDecimal, items,
                ReadOperationDate(verificationCode) ?? ReadOperationDate(query), verificationCode);
        }

        private static DateOnly? ReadOperationDate(string verificationCode) =>
            verificationCode.Length >= 15 && verificationCode.StartsWith("t=", StringComparison.OrdinalIgnoreCase)
            && verificationCode[10] is 'T' or 't'
            && DateOnly.TryParseExact(verificationCode.AsSpan(2, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
                ? date
                : null;

        private async Task<Ticket> GetTicketAsync(string query, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var credential = ConfiguredAuthentication is { } authentication
                    ? new NetworkCredential(authentication.Values.GetValueOrDefault("inn"),
                        authentication.Values.GetValueOrDefault("password"))
                    : _getCredential();
                if (credential is null || string.IsNullOrWhiteSpace(credential.UserName) || string.IsNullOrWhiteSpace(credential.Password))
                    throw new InvalidDataException("Укажите ИНН и пароль ФНС в настройках.");

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
                    return await FetchTicketAsync(query, cancellationToken);
                }
                catch (HttpRequestException exception) when (IsUnauthorized(exception))
                {
                    await RenewSessionAsync(credential, cancellationToken);
                    return await FetchTicketAsync(query, cancellationToken);
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

            return await SendJsonAsync<T>(request, "ФНС вернула пустой ответ.",
                "ФНС вернула ответ в неожиданном формате.", cancellationToken);
        }

        private static bool IsUnauthorized(HttpRequestException exception) =>
            exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

        public override void Dispose()
        {
            base.Dispose();
            _gate.Dispose();
        }
    }
}
