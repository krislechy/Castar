using System.ComponentModel;

namespace Castar.Compontents;

public sealed class UncategorizedStatusState : INotifyPropertyChanged
{
    public int Purchases { get; private set; }
    public int Incomes { get; private set; }
    public bool IsLoading { get; private set; }
    public bool IsAvailable { get; private set; }
    public string Message { get; private set; } = "";
    public bool HasPurchases => IsAvailable && Purchases > 0;
    public bool HasIncomes => IsAvailable && Incomes > 0;
    public bool Complete => IsAvailable && Purchases == 0 && Incomes == 0;
    public bool HasMissing => HasPurchases || HasIncomes;
    public string PurchaseLabel => IsAvailable ? $"Покупки · {Purchases}" : "Покупки";
    public string IncomeLabel => IsAvailable ? $"Доходы · {Incomes}" : "Доходы";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Update(bool loading, bool available, int purchases = 0, int incomes = 0, string message = "")
    {
        IsLoading = loading;
        IsAvailable = available;
        Purchases = purchases;
        Incomes = incomes;
        Message = message;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
