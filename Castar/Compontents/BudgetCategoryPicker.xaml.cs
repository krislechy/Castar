using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Castar.Api;

namespace Castar.Compontents;

public partial class BudgetCategoryPicker : UserControl
{
    public BudgetCategoryPicker() => InitializeComponent();

    internal void Load(IReadOnlyList<BudgetCategoryDto>? categories, Guid? selected)
    {
        if (categories is null)
            return;

        CategoryOption[] options =
            [new(null, "Без категории"), .. categories.Select(category => new CategoryOption(category.Id, category.Name))];
        CategoryInput.ItemsSource = options;
        CategoryInput.SelectedItem = options.FirstOrDefault(option => option.Id == selected);
        Visibility = Visibility.Visible;
    }

    internal CategorySelectionDto? GetChange(Guid? original, bool confirm = false) =>
        CategoryInput.SelectedItem is CategoryOption selected && (confirm || selected.Id != original)
            ? new CategorySelectionDto(selected.Id)
            : null;

    private sealed record CategoryOption(Guid? Id, string Name);
}
