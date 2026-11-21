using Castar.Api;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        TagChips.ItemsSource = tags;
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

    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string tag } && tags.Remove(tag))
        {
            TagWarning.Text = "";
            TagInput.Focus();
        }
    }

    private void ClearIcon_Click(object sender, RoutedEventArgs e) => IconPicker.SelectedIndex = -1;
}
