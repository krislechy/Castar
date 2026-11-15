using System.Net.Http.Headers;
using System.Text.Json;

namespace Castar.Services.Receipts;

public sealed class SerbiaReceiptProvider : IReceiptProvider, IDisposable
{
    public const string ProviderId = "serbia";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;

    public SerbiaReceiptProvider()
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
    {
    }

    internal SerbiaReceiptProvider(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
    }

    public string Id => ProviderId;

    public bool CanHandle(string query) => ParseVerificationUri(query) is not null;

    public async Task<Receipt> GetReceiptAsync(string query, CancellationToken cancellationToken)
    {
        var verificationUri = ParseVerificationUri(query)
            ?? throw new ArgumentException("Укажите ссылку проверки чека с QR-кода налоговой Сербии.", nameof(query));

        using var request = new HttpRequestMessage(HttpMethod.Get, verificationUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        InvoiceVerification? verification;
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            verification = await JsonSerializer.DeserializeAsync<InvoiceVerification>(content, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Налоговая Сербии вернула ответ в неожиданном формате.");
        }

        if (verification?.IsValid != true)
            throw new InvalidDataException("Налоговая Сербии не подтвердила подлинность чека.");

        var invoice = verification.InvoiceRequest;
        var result = verification.InvoiceResult;
        if (invoice is null || result is null || string.IsNullOrWhiteSpace(invoice.BusinessName)
            || string.IsNullOrWhiteSpace(result.InvoiceNumber) || result.SdcTime is null
            || result.TotalAmount is null || invoice.Items is null || invoice.Items.Count == 0)
        {
            throw new InvalidDataException("В ответе налоговой Сербии отсутствуют данные для сохранения чека.");
        }

        // Proforma, copy, training, advance and refund invoices cannot be recorded as purchases.
        if (invoice.InvoiceType != 0 || invoice.TransactionType != 0)
            throw new InvalidDataException("Для добавления покупки нужен чек обычной продажи.");

        var items = new List<ReceiptItem>(invoice.Items.Count);
        foreach (var item in invoice.Items)
        {
            if (item is null || item.Quantity is null || item.UnitPrice is null || item.TotalPrice is null)
                throw new InvalidDataException("В чеке отсутствуют количество или цена товара.");

            var name = string.IsNullOrWhiteSpace(item.Name) ? "Товар без названия" : item.Name;
            items.Add(new ReceiptItem(name, item.Quantity.Value, item.UnitPrice.Value, item.TotalPrice.Value));
        }

        var address = string.Join(", ", new[] { invoice.Address, invoice.City }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new Receipt(
            result.InvoiceNumber, invoice.BusinessName, invoice.LocationName, address,
            result.SdcTime.Value, "RSD", result.TotalAmount.Value, items);
    }

    private static Uri? ParseVerificationUri(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || !uri.IdnHost.Equals("suf.purs.gov.rs", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath is not ("/v/" or "/v")
            || !uri.Query.StartsWith("?vl=", StringComparison.Ordinal)
            || uri.Query.Length <= 4 || uri.Query.Contains('&'))
        {
            return null;
        }

        return uri;
    }

    public void Dispose() => _client.Dispose();

    private sealed record InvoiceVerification(bool? IsValid, InvoiceRequest? InvoiceRequest, InvoiceResult? InvoiceResult);

    private sealed record InvoiceRequest(
        string? BusinessName, string? LocationName, string? Address, string? City,
        int? InvoiceType, int? TransactionType, List<InvoiceItem?>? Items);

    private sealed record InvoiceItem(string? Name, decimal? Quantity, decimal? UnitPrice, decimal? TotalPrice);

    private sealed record InvoiceResult(string? InvoiceNumber, DateTimeOffset? SdcTime, decimal? TotalAmount);
}
