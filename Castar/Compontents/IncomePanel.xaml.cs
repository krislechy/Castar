using Castar.Domain.Models.DataBase;
using Castar.Currency;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents
{
    /// <summary>
    /// Логика взаимодействия для IncomePanel.xaml
    /// </summary>
    public partial class IncomePanel : UserControl
    {
        public static readonly DependencyProperty UsersProperty = DependencyProperty.Register(nameof(Users), typeof(IEnumerable<User>), typeof(IncomePanel));
        public IEnumerable<User> Users
        {
            get
            {
                return (IEnumerable<User>)GetValue(UsersProperty);
            }
            set
            {
                SetValue(UsersProperty, value);
            }
        }
        public IReadOnlyList<string> CurrencyOptions { get; }

        public IncomePanel() : this(null)
        {
        }

        internal IncomePanel(Income? income)
        {
            var currencies = HistoricalExchangeRates.SupportedCurrencies;
            CurrencyOptions = income?.Currency is { } currency && !currencies.Contains(currency)
                ? currencies.Append(currency).ToArray() : currencies;
            InitializeComponent();
            DataContext = income;
        }

        internal bool ValidateAmount()
        {
            IncomeAmount.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            return !Validation.GetHasError(IncomeAmount);
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is Income income)
                UsersListView.SetCurrentValue(ListView.SelectedValueProperty, Users?.SingleOrDefault(user => user.Id == income.UserId));
        }
    }
}
