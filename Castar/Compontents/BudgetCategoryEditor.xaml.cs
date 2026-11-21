using Castar.Api;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class BudgetCategoryEditor : UserControl
{
    internal BudgetCategoryEditor(string kind, BudgetCategoryDto? category)
    {
        InitializeComponent();
        NameInput.Text = category?.Name ?? "";
        IconPicker.ItemsSource = BudgetCategoryIcons.ForKind(kind);
        IconPicker.SelectedValue = BudgetCategoryIconKeys.Canonicalize(kind, category?.IconKey);
    }

    internal string CategoryName => NameInput.Text.Trim();
    internal string? IconKey => IconPicker.SelectedValue as string;

    private void ClearIcon_Click(object sender, RoutedEventArgs e) => IconPicker.SelectedIndex = -1;
}
