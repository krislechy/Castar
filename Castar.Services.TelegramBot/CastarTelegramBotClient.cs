using Castar.BL;
using Castar.BL.Settings;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts;
using Castar.Services.Receipts.Barcode;
using Castar.Services.Receipts.Models;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace Castar.Services.TelegramBot
{
    public sealed class CastarTelegramBotClient : BaseCastarTelegramBotClient, ICastarTelegramBotClient
    {
        private ITelegramBotClient telegramBotClient;
        private CancellationTokenSource cts;
        public CastarTelegramBotClient(
            ISettingsService settingsService,
            IPurchasesService purchasesService,
            IItemsService itemsService,
            IIncomesService incomesService,
            IUsersService usersService)
        {
            base.settingsService = settingsService;
            base.settings = settingsService.Get();
            base.incomesService = incomesService;
            base.usersService = usersService;
            base.purchasesService = purchasesService;
            base.itemsService = itemsService;
            settings.Systems.PropertyChanged += Systems_PropertyChanged;
            settings.TelegramBot.PropertyChanged += TelegramBot_PropertyChanged;
            StartReceiving();
        }

        private async void TelegramBot_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var telegramBotHanlder = (Castar.Domain.Models.SettingsModel.TelegramBot)sender!;
            var action = e.PropertyName switch
            {
                "TelegramBotApi_KEY" => Task.Factory.StartNew(() =>
                {
                    CloseTbc();
                    StartReceiving();
                }),
                _ => Task.CompletedTask,
            };
            await action;
        }

        private async void Systems_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var systemSettings = (Systems)sender!;
            var action = e.PropertyName switch
            {
                "IsEnableTelegramBot" => Task.Factory.StartNew(() =>
                {
                    if (systemSettings.IsEnableTelegramBot)
                    {
                        CloseTbc();
                        StartReceiving();
                    }
                    else
                        CloseTbc();
                }),
                _ => Task.CompletedTask,
            };
            await action;
        }
        private async void CloseTbc()
        {
            settings.TelegramBot.Status = "Отключение...";
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            if (telegramBotClient != null)
            {
                try
                {
                    await telegramBotClient.CloseAsync();
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException ex)
                {

                }
                telegramBotClient = null;
            }
            settings.TelegramBot.Status = "Отключено";
        }
        private void StartReceiving()
        {
            if (!String.IsNullOrEmpty(settings.TelegramBot.TelegramBotApi_KEY))
            {
                if (settings.Systems.IsEnableTelegramBot)
                {
                    settings.TelegramBot.Status = "Подключение...";
                    this.telegramBotClient = new TelegramBotClient(settings.TelegramBot.TelegramBotApi_KEY);
                    cts = new CancellationTokenSource();
                    var receiverOptions = new ReceiverOptions()
                    {
                        AllowedUpdates = Array.Empty<UpdateType>(),
                        ThrowPendingUpdates = true,
                    };
                    telegramBotClient.StartReceiving(updateHandler: HandleUpdateAsync,
                           pollingErrorHandler: PollingErrorHandler,
                           receiverOptions: receiverOptions,
                           cancellationToken: cts.Token);
                    settings.TelegramBot.Status = "Подключено";
                }
                else
                    settings.TelegramBot.Status = "Отключено";
            }
            else
            {
                settings.Systems.IsEnableTelegramBot = false;
                settings.TelegramBot.Status = "Токен не заполнен";
            }
        }
    }
}