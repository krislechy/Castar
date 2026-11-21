namespace Castar.Api;

public sealed record BudgetCategoryDto(Guid Id, string Kind, string Name, string? IconKey = null);

public sealed record BudgetCategoryWriteDto(string Kind, string Name, string? IconKey = null);

public sealed record CategorySelectionDto(Guid? Id);
