namespace Castar.Domain.Models.DataBase;

public sealed class PurchaseAdjustment
{
    public Guid PurchaseId { get; set; }
    public string? Currency { get; set; }
    public decimal Amount { get; set; }
}
