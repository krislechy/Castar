using System.Collections.ObjectModel;

namespace Castar.Domain.Models.DataBase
{
    public class Purchase : BaseDbModel, IPurchase
    {
        private long? _messageId;
        private string? _inputCurrency;
        private decimal? _inputTotal;

        public User User { get; private set; } = null!;
        public Guid UserId { get; set; }
        public Guid? TelegramOriginUserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal TotalSum
        {
            get
            {
                if (_inputTotal is { } total)
                    return total;
                var amounts = GetAmountsByCurrency();
                return amounts.Length == 1 ? amounts[0].Amount : 0m;
            }
            set => _inputTotal = value;
        }
        public long MessageId
        {
            get => _messageId.GetValueOrDefault();
            set => _messageId = value == 0 ? null : value;
        }

        public string? RetailPlace { get; set; }
        public string? RetailPlaceAddress { get; set; }
        public byte[]? RawData { get; set; }
        public string? ReceiptSource { get; set; }
        public string? OriginalReceiptSnapshot { get; set; }
        public string? Currency
        {
            get
            {
                if (_inputCurrency is { } currency)
                    return currency;
                var amounts = GetAmountsByCurrency();
                return amounts.Length == 1 ? amounts[0].Currency : null;
            }
            set => _inputCurrency = value;
        }
        public PurchaseAdjustment? Adjustment { get; set; }
        public DateOnly? ReceiptDate { get; set; }
        public DateOnly? RateDate { get; set; }
        public long Revision { get; set; }
        public ObservableCollection<Item> Items { get; set; } = new();
        public DateTime? AddedOn { get; set; }

        public void RecalculateManualTotals()
        {
            foreach (var item in Items)
            {
                item.Currency ??= _inputCurrency;
                (item.Price, item.Sum) = CalculateManualItemAmounts(item.Quantity, item.Price, item.Currency);
            }
        }

        public (decimal Amount, string? Currency)[] GetAmountsByCurrency()
        {
            var amounts = Items.Select(item => (Amount: item.Sum, Currency: item.Currency ?? _inputCurrency));
            if (Adjustment is { } adjustment && (adjustment.Amount != 0m || Items.Count == 0))
                amounts = amounts.Append((adjustment.Amount, adjustment.Currency));
            return amounts.GroupBy(entry => entry.Currency)
                .Select(group => (Amount: group.Sum(entry => entry.Amount), Currency: group.Key)).ToArray();
        }

        public bool HasMultipleCurrencies() => GetAmountsByCurrency().Length > 1;

        public void PreserveImportedTotal()
        {
            foreach (var item in Items)
                item.Currency ??= _inputCurrency;
            if (_inputTotal is { } total)
            {
                var difference = total - Items.Sum(item => item.Sum);
                if (difference != 0 || Items.Count == 0)
                    Adjustment = new() { PurchaseId = Id, Amount = difference, Currency = _inputCurrency };
            }
        }

        public (decimal Price, decimal Sum) CalculateManualItemAmounts(double quantity, decimal price,
            string? itemCurrency = null)
        {
            var legacyPrecision = (itemCurrency ?? _inputCurrency) is null;
            if (legacyPrecision)
                price = decimal.Round(price, 2, MidpointRounding.AwayFromZero);
            var sum = price * (decimal)quantity;
            return (price, legacyPrecision ? decimal.Round(sum, 2, MidpointRounding.AwayFromZero) : sum);
        }
    }
}
