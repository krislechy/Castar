namespace Castar.DAL;

public sealed class ExchangeRateEntry
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public DateOnly Date { get; init; }
    public DateOnly QuotedOn { get; set; }
    public decimal Rate { get; set; }
    public DateTime RetrievedUtc { get; set; }
}
