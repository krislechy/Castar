namespace Castar.Domain.Models.DataBase;

public sealed class BudgetCategory : BaseDbModel
{
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string NormalizedName { get; init; } = "";
}
