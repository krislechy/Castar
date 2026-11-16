using System.Net.Http.Json;
using System.Text.Json;

namespace Castar.Services.Receipts;

public abstract record ReceiptIdentitySource;

public abstract class ReceiptProvider : IReceiptProvider, IDisposable
{
    protected ReceiptProvider(HttpMessageHandler handler, Uri? baseAddress = null)
    {
        Client = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
    }

    protected HttpClient Client { get; }

    public abstract string Id { get; }

    public abstract bool CanHandle(string query);

    public async Task<Receipt> GetReceiptAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = query.Trim();
        if (!CanHandle(normalized))
            throw new ArgumentException("Код чека не поддерживается выбранным поставщиком.", nameof(query));

        var details = await FetchReceiptAsync(normalized, cancellationToken);
        var identity = GetReceiptIdentity(details.Identity);
        if (string.IsNullOrWhiteSpace(identity))
            throw new InvalidDataException("В ответе поставщика отсутствует номер чека.");
        if (details.Currency is not { Length: 3 } || !details.Currency.All(character => character is >= 'A' and <= 'Z'))
            throw new InvalidDataException("В ответе поставщика отсутствует корректная валюта чека.");

        return new Receipt(identity, details.Name, details.RetailPlace,
            details.RetailPlaceAddress, details.IssuedAt, details.Currency, details.Total, details.Items,
            details.LegacyVerificationCode)
        {
            Source = query,
            OperationDate = details.OperationDate ?? DateOnly.FromDateTime(details.IssuedAt.Date)
        };
    }

    public abstract string GetReceiptIdentity(ReceiptIdentitySource source);

    protected abstract Task<ReceiptDetails> FetchReceiptAsync(string query, CancellationToken cancellationToken);

    protected sealed record ReceiptDetails(ReceiptIdentitySource Identity, string Name, string? RetailPlace,
        string? RetailPlaceAddress, DateTimeOffset IssuedAt, string Currency, decimal Total,
        IReadOnlyList<ReceiptItem> Items, DateOnly? OperationDate = null, string? LegacyVerificationCode = null);

    protected static SocketsHttpHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    protected async Task<T> SendJsonAsync<T>(HttpRequestMessage request, string emptyResponseMessage,
        string invalidFormatMessage, CancellationToken cancellationToken) where T : class
    {
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
                ?? throw new InvalidDataException(emptyResponseMessage);
        }
        catch (JsonException)
        {
            throw new InvalidDataException(invalidFormatMessage);
        }
    }

    protected static string NormalizeItemName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Товар без названия" : name;

    public virtual void Dispose() => Client.Dispose();
}
