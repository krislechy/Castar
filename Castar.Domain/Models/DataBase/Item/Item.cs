using System.Collections.ObjectModel;

namespace Castar.Domain.Models.DataBase
{
    public class Item : BaseDbModel, IItem
    {
        public Purchase? Purchase { get; set; }
        public Guid PurchaseId { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal Sum { get; set; }
        public ObservableCollection<User> Users { get; set; } = new();
    }
}
