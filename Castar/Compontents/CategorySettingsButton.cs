using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public sealed class CategorySettingsButton : Button
{
    public string Kind { get; set; } = "";

    protected override async void OnClick()
    {
        base.OnClick();
        if (Window.GetWindow(this) is MainWindow window)
            await window.OpenCategorySettingsAsync(Kind);
    }
}
