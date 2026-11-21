namespace Castar.Api;

public sealed record BudgetCategoryDto(Guid Id, string Kind, string Name);

public sealed record BudgetCategoryWriteDto(string Kind, string Name);

public sealed record CategorySelectionDto(Guid? Id);
