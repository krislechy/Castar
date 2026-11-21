namespace Castar.Domain.Models.DataBase;

public sealed class IncomeCategoryHistory
{
    public string Key { get; init; } = "";
    public Guid? CategoryId { get; set; }
}
