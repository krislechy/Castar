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

    public abstract Task<Receipt> GetReceiptAsync(string query, CancellationToken cancellationToken);

    public abstract string GetReceiptIdentity(ReceiptIdentitySource source);

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

    protected Receipt CreateReceipt(ReceiptIdentitySource source, string name, string? retailPlace,
        string? retailPlaceAddress, DateTimeOffset issuedAt, string currency, decimal total,
        IReadOnlyList<ReceiptItem> items, string? legacyVerificationCode = null) =>
        new(GetReceiptIdentity(source), name, retailPlace, retailPlaceAddress, issuedAt, currency, total, items,
            legacyVerificationCode);

    protected static string NormalizeItemName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Товар без названия" : name;

    public virtual void Dispose() => Client.Dispose();
}
