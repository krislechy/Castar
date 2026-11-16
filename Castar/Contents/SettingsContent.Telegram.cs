using Castar.Compontents;
using System.Windows;
using System.Windows.Media;

namespace Castar.Contents;

public partial class SettingsContent
{
    private async void ConfigureTelegramPreferences_Click(object sender, RoutedEventArgs e)
    {
        var viewModel = context.ServerConfiguration;
        if (!viewModel.CanEdit || viewModel.TelegramPreferences is not { } original)
            return;

        var panel = new TelegramPreferencesEditor(original);
        var accepted = await PopupDialog.Show(panel, PopupDialog.TypeDialogEnum.Confirm, options =>
        {
            options.Title = "Настройки Telegram";
            options.Background = (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
            options.SuccessButton.SuccessConfirmButtonText = "Применить";
            options.AllowMissClick = false;
            return options;
        }, panel.Validate);

        if (accepted == true && panel.Result is { } result)
            viewModel.SetTelegramPreferences(original, result);
    }
}
