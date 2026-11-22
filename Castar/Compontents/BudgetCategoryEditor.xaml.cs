using Castar.Api;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Castar.Compontents;

public partial class BudgetCategoryEditor : UserControl
{
    private readonly ObservableCollection<string> tags = [];

    internal BudgetCategoryEditor(string kind, BudgetCategoryDto? category)
    {
        InitializeComponent();
        NameInput.Text = category?.Name ?? "";
        IconPicker.ItemsSource = BudgetCategoryIcons.ForKind(kind);
        IconPicker.SelectedValue = BudgetCategoryIconKeys.Canonicalize(kind, category?.IconKey);
        foreach (var tag in category?.Tags ?? [])
            tags.Add(tag);
        TagsCount.DataContext = tags;
    }

    internal string CategoryName => NameInput.Text.Trim();
    internal string? IconKey => IconPicker.SelectedValue as string;
    internal IReadOnlyList<string> Tags => tags;

    internal string? CommitPendingTag()
    {
        TagWarning.Text = "";
        string tag;
        try
        {
            tag = TagInput.Text.Trim().Normalize();
        }
        catch (ArgumentException)
        {
            return "Тег содержит недопустимые символы.";
        }
        if (tag.Length == 0 || tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            TagInput.Clear();
            return null;
        }
        if (tag.Length > BudgetCategoryTagLimits.MaxLength || tag.Any(char.IsControl))
            return $"Укажите тег длиной до {BudgetCategoryTagLimits.MaxLength} символов без переноса строки.";
        if (tags.Count >= BudgetCategoryTagLimits.MaxCount)
            return $"Можно добавить до {BudgetCategoryTagLimits.MaxCount} тегов.";

        tags.Add(tag);
        TagInput.Clear();
        return null;
    }

    private void TagInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        TagWarning.Text = CommitPendingTag() ?? "";
    }

    private void TagInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TagWarning is not null)
            TagWarning.Text = "";
    }

    private async void ManageTags_Click(object sender, RoutedEventArgs e)
    {
        var list = new BudgetCategoryTagsContent(tags);
        var accepted = await PopupDialog.ShowNested(this, list, options =>
        {
            options.Title = "Теги";
            options.EditorIcon = (Geometry)FindResource("Lucide.tags");
            options.SuccessButton.SuccessConfirmButtonText = "Готово";
            options.AllowMissClick = false;
            return options;
        });
        if (accepted != true)
            return;

        foreach (var removed in tags.Except(list.Tags).ToArray())
            tags.Remove(removed);
        TagWarning.Text = "";
    }

    private void ClearIcon_Click(object sender, RoutedEventArgs e) => IconPicker.SelectedIndex = -1;
}
