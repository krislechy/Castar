using Castar.Domain.Models.DataBase;
using Castar.Currency;
using Castar.Api;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Castar.Compontents
{
    /// <summary>
    /// Логика взаимодействия для IncomePanel.xaml
    /// </summary>
    public partial class IncomePanel : UserControl
    {
        private readonly DateTime? originalTime;

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

        internal IncomePanel(Income? income, IReadOnlyList<string>? enabledCurrencies = null,
            IReadOnlyList<BudgetCategoryDto>? categories = null)
        {
            var currencies = enabledCurrencies ?? HistoricalExchangeRates.SupportedCurrencies;
            CurrencyOptions = income?.Currency is { } currency && !currencies.Contains(currency)
                ? currencies.Append(currency).ToArray() : currencies;
            InitializeComponent();
            DataContext = income;
            originalTime = income?.AddedOn is { } addedOn && addedOn != default ? addedOn : null;
            IncomeDate.SelectedDate = originalTime?.Date;
            IncomeTime.Text = CastarDatePicker.FormatTime(originalTime);
            ApplyEditorPresentation();
            IncomeCategory.Load(categories, income?.CategoryId);
        }

        internal Geometry EditorIcon => (Geometry)FindResource("Lucide.briefcase-business");
        internal CategorySelectionDto? CategoryChange
        {
            get
            {
                var income = DataContext as Income;
                return IncomeCategory.GetChange(income?.CategoryId,
                    confirm: income is { CategoryId: not null, CategoryState: not IncomeCategoryState.Confirmed });
            }
        }

        private void ApplyEditorPresentation()
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/Castar;component/Styles/IncomeCreateForm.xaml", UriKind.Relative)
            });
            IncomeLayout.MinWidth = 536;
            OwnerColumn.Width = new GridLength(184);
            IncomeFields.Margin = new Thickness(0);
            IncomeFields.Children.Clear();
            IncomeLayout.Children.Remove(UsersPicker);
            var owners = new Border
            {
                Child = UsersPicker, Margin = new Thickness(0, 0, 16, 0), Padding = new Thickness(8, 0, 8, 8),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top
            };
            owners.SetResourceReference(BackgroundProperty, "PurchaseForm.OwnerSurface");
            owners.SetResourceReference(Border.BorderBrushProperty, "PurchaseForm.PanelBorder");
            IncomeLayout.Children.Add(owners);
            UsersPicker.ApplyListStyle((Style)FindResource("PurchaseFormOwnerList"));
            IncomeFields.Children.Add(Field(IncomeSourceLabel, IncomeSource, "Lucide.briefcase-business", "Например, Зарплата"));
            IncomeFields.Children.Add(Pair(
                Field(IncomeSumLabel, IncomeAmount, "Lucide.banknote"),
                Field(IncomeCurrencyLabel, IncomeCurrency, "Lucide.coins")));
            IncomeFields.Children.Add(Pair(
                Field(IncomeDateLabel, IncomeDate),
                Field(IncomeTimeLabel, IncomeTime, "Lucide.clock")));
            IncomeFields.Children.Add(IncomeCategory);
            IncomeDate.SetResourceReference(ForegroundProperty, "PurchaseForm.Foreground");
            IncomeDate.SetResourceReference(BorderBrushProperty, "PurchaseForm.FieldBorder");
        }

        private FrameworkElement Field(TextBlock label, Control input, string? icon = null, string? placeholder = null)
        {
            var field = new StackPanel();
            field.Children.Add(label);
            var body = new Grid();
            body.Children.Add(input);
            if (icon is not null)
            {
                input.Padding = new Thickness(34, 7, 8, 7);
                if (input is TextBox text)
                {
                    text.Tag = true;
                    text.SetResourceReference(StyleProperty, "PurchaseFormTextBox");
                    if (placeholder is not null)
                    {
                        var hint = new TextBlock { Text = placeholder, Tag = text };
                        hint.SetResourceReference(StyleProperty, "PurchaseFormPlaceholder");
                        body.Children.Add(hint);
                    }
                }
                var glyph = new Path { Data = (Geometry)FindResource(icon) };
                glyph.SetResourceReference(StyleProperty, "PurchaseFormFieldIcon");
                body.Children.Add(glyph);
            }
            field.Children.Add(body);
            return field;
        }

        private static Grid Pair(FrameworkElement first, FrameworkElement second)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(first);
            Grid.SetColumn(second, 2);
            row.Children.Add(second);
            return row;
        }

        internal bool ValidateAmount()
        {
            IncomeAmount.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            return !Validation.GetHasError(IncomeAmount);
        }

        internal bool ValidateDate()
        {
            if (DataContext is not Income income || !IncomeDate.TryGetDateTime(IncomeTime.Text, originalTime, out var date))
                return false;
            income.AddedOn = date;
            return true;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is Income income)
                UsersPicker.SetCurrentValue(BudgetUserPicker.SelectedUserProperty, Users?.SingleOrDefault(user => user.Id == income.UserId));
        }
    }
}
