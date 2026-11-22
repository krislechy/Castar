using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class BudgetCategoryTagsContent : UserControl
{
    private readonly ObservableCollection<string> tags;

    internal BudgetCategoryTagsContent(IEnumerable<string> tags)
    {
        InitializeComponent();
        this.tags = new(tags);
        TagsList.ItemsSource = this.tags;
    }

    internal IReadOnlyList<string> Tags => tags;

    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string tag })
            tags.Remove(tag);
    }
}
