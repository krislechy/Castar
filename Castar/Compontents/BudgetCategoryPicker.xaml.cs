using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Castar.Api;

namespace Castar.Compontents;

public partial class BudgetCategoryPicker : UserControl
{
    public BudgetCategoryPicker() => InitializeComponent();

    private bool compact;

    internal void Load(IReadOnlyList<BudgetCategoryDto>? categories, Guid? selected, bool compact = false)
    {
        if (categories is null)
            return;

        CategoryOption[] options =
            [new(null, "Без категории", null),
                .. categories.Select(category => new CategoryOption(category.Id, category.Name, category.IconKey))];
        CategoryInput.ItemsSource = options;
        CategoryInput.SelectedItem = options.FirstOrDefault(option => option.Id == selected);
        this.compact = compact;
        if (compact)
        {
            CategoryLabel.Visibility = Visibility.Collapsed;
            CategoryInput.Width = 40;
            CategoryInput.IsTabStop = false;
            CategoryInput.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            AutomationProperties.SetLabeledBy(CategoryInput, null);
            UpdateCompactGlyph();
        }
        Visibility = Visibility.Visible;
    }

    private void CategoryInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (compact)
            UpdateCompactGlyph();
    }

    private void UpdateCompactGlyph()
    {
        var selected = CategoryInput.SelectedItem as CategoryOption;
        var assigned = selected?.Id.HasValue == true;
        CastarComboBox.SetCompactGlyph(CategoryInput, BudgetCategoryIcons.ForKey(selected?.IconKey));
        CategoryInput.SetResourceReference(ForegroundProperty, assigned ? "Highlight" : "PurchaseForm.Muted");
        var label = assigned ? $"Категория: {selected!.Name}" : "Выбрать категорию";
        CategoryInput.ToolTip = label;
        AutomationProperties.SetName(CategoryInput, label);
    }

    internal CategorySelectionDto? GetChange(Guid? original, bool confirm = false) =>
        CategoryInput.SelectedItem is CategoryOption selected && (confirm || selected.Id != original)
            ? new CategorySelectionDto(selected.Id)
            : null;

    private sealed record CategoryOption(Guid? Id, string Name, string? IconKey);
}
