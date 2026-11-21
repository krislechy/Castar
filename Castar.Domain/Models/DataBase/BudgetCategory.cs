namespace Castar.Domain.Models.DataBase;

public sealed class BudgetCategory : BaseDbModel
{
    public string Kind { get; init; } = "";
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? IconKey { get; set; }
}
