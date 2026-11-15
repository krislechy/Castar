using System.Collections.ObjectModel;

namespace Castar.Domain.Models.DataBase
{
    public class Purchase : BaseDbModel, IPurchase
    {
        private long? _messageId;

        public User User { get; private set; } = null!;
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal TotalSum { get; set; }
        public long MessageId
        {
            get => _messageId.GetValueOrDefault();
            set => _messageId = value == 0 ? null : value;
        }

        public string? RetailPlace { get; set; }
        public string? RetailPlaceAddress { get; set; }
        public byte[]? RawData { get; set; }
        public string? Currency { get; set; }
        public DateOnly? ReceiptDate { get; set; }
        public ObservableCollection<Item> Items { get; set; } = new();
        public DateTime? AddedOn { get; set; }

        public void RecalculateManualTotals()
        {
            foreach (var item in Items)
            {
                if (Currency is null)
                    item.Price = decimal.Round(item.Price, 2, MidpointRounding.AwayFromZero);
                var sum = item.Price * (decimal)item.Quantity;
                item.Sum = Currency is null ? decimal.Round(sum, 2, MidpointRounding.AwayFromZero) : sum;
            }

            TotalSum = Items.Sum(x => x.Sum);
        }
    }
}
