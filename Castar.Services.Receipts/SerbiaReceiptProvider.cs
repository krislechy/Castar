using System.Globalization;
using System.Net.Http.Headers;

namespace Castar.Services.Receipts;

public sealed class SerbiaReceiptProvider : ReceiptProvider
{
    public const string ProviderId = "serbia";
    private static readonly TimeZoneInfo? LocalTimeZone =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Belgrade", out var zone) ? zone : null;

    public sealed record InvoiceIdentity(string? InvoiceNumber) : ReceiptIdentitySource;

    public SerbiaReceiptProvider() : this(CreateHttpHandler())
    {
    }

    internal SerbiaReceiptProvider(HttpMessageHandler handler) : base(handler)
    {
    }

    public override string Id => ProviderId;

    public override bool CanHandle(string query) => ParseVerificationUri(query) is not null;

    public override string GetReceiptIdentity(ReceiptIdentitySource source)
    {
        if (source is not InvoiceIdentity invoice)
            throw new ArgumentException("Ожидается идентификатор чека налоговой Сербии.", nameof(source));
        if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
            throw new InvalidDataException("В ответе налоговой Сербии отсутствует номер чека.");

        return invoice.InvoiceNumber;
    }

    protected override async Task<ReceiptDetails> FetchReceiptAsync(string query, CancellationToken cancellationToken)
    {
        var verificationUri = ParseVerificationUri(query)
            ?? throw new ArgumentException("Укажите ссылку проверки чека с QR-кода налоговой Сербии.", nameof(query));

        using var request = new HttpRequestMessage(HttpMethod.Get, verificationUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var verification = await SendJsonAsync<InvoiceVerification>(request,
            "Налоговая Сербии не подтвердила подлинность чека.",
            "Налоговая Сербии вернула ответ в неожиданном формате.", cancellationToken);

        if (verification.IsValid != true)
            throw new InvalidDataException("Налоговая Сербии не подтвердила подлинность чека.");

        var invoice = verification.InvoiceRequest;
        var result = verification.InvoiceResult;
        if (invoice is null || result is null || string.IsNullOrWhiteSpace(invoice.BusinessName)
            || string.IsNullOrWhiteSpace(result.InvoiceNumber) || result.SdcTime is null
            || result.TotalAmount is null)
        {
            throw new InvalidDataException("В ответе налоговой Сербии отсутствуют данные для сохранения чека.");
        }

        // Proforma, copy, training, advance and refund invoices cannot be recorded as purchases.
        if (invoice.InvoiceType != 0 || invoice.TransactionType != 0)
            throw new InvalidDataException("Для добавления покупки нужен чек обычной продажи.");

        var items = invoice.Items is { Count: > 0 } structuredItems
            ? ReadStructuredItems(structuredItems)
            : SerbiaReceiptJournal.ReadItems(verification.Journal, result.TotalAmount.Value);
        var address = string.Join(", ", new[] { invoice.Address, invoice.City }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var issuedAt = result.SdcTime.Value;
        return new ReceiptDetails(new InvoiceIdentity(result.InvoiceNumber), invoice.BusinessName, invoice.LocationName,
            address, issuedAt, "RSD", result.TotalAmount.Value, items,
            GetOperationDate(issuedAt, LocalTimeZone, verification.Journal));
    }

    private static DateOnly GetOperationDate(DateTimeOffset issuedAt, TimeZoneInfo? zone, string? journal)
    {
        if (zone is not null)
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(issuedAt, zone).Date);

        // The fiscal journal also provides the printed date when the IANA zone is unavailable.
        if (journal is not null)
        {
            using var reader = new StringReader(journal);
            while (reader.ReadLine() is { } line)
            {
                var separator = line.IndexOf(':');
                if (separator < 0 || line[..separator].Trim() is not ("ПФР време" or "PFR vreme" or "SDC Time"))
                    continue;
                var timestamp = line.AsSpan(separator + 1).Trim();
                var timeSeparator = timestamp.IndexOfAny(' ', '\t');
                var date = timeSeparator < 0 ? timestamp : timestamp[..timeSeparator];
                if (DateOnly.TryParseExact(date, ["d.M.yyyy.", "d.M.yyyy", "M/d/yyyy", "yyyy-MM-dd"],
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var printedDate))
                    return printedDate;
            }
        }

        return DateOnly.FromDateTime(issuedAt.Date);
    }

    private static List<ReceiptItem> ReadStructuredItems(List<InvoiceItem?> structuredItems)
    {
        var items = new List<ReceiptItem>(structuredItems.Count);
        foreach (var item in structuredItems)
        {
            if (item is null || item.Quantity is null || item.UnitPrice is null || item.TotalPrice is null)
                throw new InvalidDataException("В чеке отсутствуют количество или цена товара.");

            items.Add(new ReceiptItem(NormalizeItemName(item.Name), item.Quantity.Value,
                item.UnitPrice.Value, item.TotalPrice.Value));
        }

        return items;
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

    private sealed record InvoiceVerification(bool? IsValid, InvoiceRequest? InvoiceRequest, InvoiceResult? InvoiceResult,
        string? Journal);

    private sealed record InvoiceRequest(
        string? BusinessName, string? LocationName, string? Address, string? City,
        int? InvoiceType, int? TransactionType, List<InvoiceItem?>? Items);

    private sealed record InvoiceItem(string? Name, decimal? Quantity, decimal? UnitPrice, decimal? TotalPrice);

    private sealed record InvoiceResult(string? InvoiceNumber, DateTimeOffset? SdcTime, decimal? TotalAmount);
}
