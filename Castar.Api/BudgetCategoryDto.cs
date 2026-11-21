namespace Castar.Api;

public sealed record BudgetCategoryDto(Guid Id, string Kind, string Name, string? IconKey = null,
    IReadOnlyList<string>? Tags = null);

public sealed record BudgetCategoryWriteDto(string Kind, string Name, string? IconKey = null,
    IReadOnlyList<string>? Tags = null);

public sealed record CategorySelectionDto(Guid? Id);

public static class BudgetCategoryTagLimits
{
    public const int MaxCount = 20;
    public const int MaxLength = 60;
}
