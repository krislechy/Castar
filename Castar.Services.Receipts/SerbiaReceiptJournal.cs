using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Castar.Services.Receipts;

internal static partial class SerbiaReceiptJournal
{
    private static readonly NumberFormatInfo Serbian = new() { NumberGroupSeparator = ".", NumberDecimalSeparator = "," };
    private static readonly NumberFormatInfo English = NumberFormatInfo.InvariantInfo;
    private static readonly Regex SerbianRow = CreateRowPattern(Serbian);
    private static readonly Regex EnglishRow = CreateRowPattern(English);

    internal static IReadOnlyList<ReceiptItem> ReadItems(string? journal, decimal invoiceTotal)
    {
        if (string.IsNullOrWhiteSpace(journal))
            throw new InvalidDataException("Налоговая Сербии не вернула позиции чека.");

        using var reader = new StringReader(journal);
        while (reader.ReadLine() is { } line)
        {
            var header = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            switch (header.ToUpperInvariant())
            {
                case "НАЗИВ ЦЕНА КОЛ. УКУПНО":
                case "NAZIV CENA KOL. UKUPNO":
                    return ReadRows(reader, SerbianRow, Serbian, invoiceTotal);
                case "NAME PRICE QTY. TOTAL":
                    return ReadRows(reader, EnglishRow, English, invoiceTotal);
            }
        }

        throw new InvalidDataException("В журнале налоговой Сербии не найдена таблица товаров.");
    }

    private static IReadOnlyList<ReceiptItem> ReadRows(StringReader reader, Regex rowPattern, NumberFormatInfo numberFormat,
        decimal invoiceTotal)
    {
        var items = new List<ReceiptItem>();
        StringBuilder? pendingName = null;
        while (reader.ReadLine() is { } source)
        {
            var line = source.Trim();
            if (line.Length == 0)
                continue;
            if (line.All(character => character == '-') || line.All(character => character == '='))
            {
                if (items.Count == 0 && pendingName is null)
                    continue;
                if (pendingName is not null)
                    break;
                if (items.Sum(item => item.Total) != invoiceTotal)
                    throw new InvalidDataException("Сумма позиций в журнале налоговой Сербии не совпадает с итогом чека.");
                return items;
            }

            var row = rowPattern.Match(line);
            if (!row.Success)
            {
                if (line.All(character => char.IsAsciiDigit(character) || char.IsWhiteSpace(character)
                    || character is '-' or '.' or ','))
                    break;
                if (pendingName is null)
                    pendingName = new StringBuilder(line);
                else
                    pendingName.Append(' ').Append(line);
                continue;
            }

            var inlineName = row.Groups["name"];
            if (pendingName is not null && inlineName.Success)
                break;
            var name = pendingName?.ToString() ?? (inlineName.Success ? inlineName.Value : null);
            if (name is null
                || !TryReadNumber(row, "price", numberFormat, out var price)
                || !TryReadNumber(row, "quantity", numberFormat, out var quantity)
                || !TryReadNumber(row, "total", numberFormat, out var total))
                break;

            name = TaxLabels().Replace(name, "").TrimEnd();
            items.Add(new ReceiptItem(string.IsNullOrWhiteSpace(name) ? "Товар без названия" : name,
                quantity, price, total));
            pendingName = null;
        }

        throw new InvalidDataException("В журнале налоговой Сербии не удалось прочитать все позиции чека.");
    }

    private static Regex CreateRowPattern(NumberFormatInfo numberFormat)
    {
        var groupSeparator = Regex.Escape(numberFormat.NumberGroupSeparator);
        var decimalSeparator = Regex.Escape(numberFormat.NumberDecimalSeparator);
        var number = $@"-?(?:[0-9]{{1,3}}(?:{groupSeparator}[0-9]{{3}})+|[0-9]+)(?:{decimalSeparator}[0-9]+)?";
        return new Regex($@"^(?:(?<name>.+?\))\s+)?(?<price>{number})\s+(?<quantity>{number})\s+(?<total>{number})$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static bool TryReadNumber(Match row, string column, NumberFormatInfo numberFormat, out decimal value) =>
        decimal.TryParse(row.Groups[column].ValueSpan,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint, numberFormat, out value);

    [GeneratedRegex(@"\s+\(\p{Lu}(?:,\s*\p{Lu})*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex TaxLabels();
}
