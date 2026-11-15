namespace Castar.Api;

public sealed record BudgetEventDto(string Kind, bool Created);

public static class BudgetEvents
{
    public const string Path = "api/events";
    public const string Method = "BudgetChanged";
    public const string Ready = "BudgetReady";
    public const string Purchase = "purchase";
    public const string Income = "income";
}
