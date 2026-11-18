using Castar.Compontents;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Contents;

public partial class SettingsContent
{
    private void ConfigureTelegramPreferences_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = context.ServerConfiguration;
        if (!viewModel.CanEdit || viewModel.TelegramPreferences is not { } original)
            return;

        var panel = new TelegramPreferencesEditor(original);
        OpenNestedSettings((Button)sender, "Значки Telegram", panel,
            () => viewModel.CanEdit && ReferenceEquals(viewModel.TelegramPreferences, original), panel.Validate,
            () => viewModel.SetTelegramPreferences(original, panel.Result!));
    }
}
