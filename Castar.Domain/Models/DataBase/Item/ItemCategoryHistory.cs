namespace Castar.Domain.Models.DataBase;

public sealed class ItemCategoryHistory
{
    public string Key { get; set; } = string.Empty;
    public Guid? BudgetCategoryId { get; set; }
}
