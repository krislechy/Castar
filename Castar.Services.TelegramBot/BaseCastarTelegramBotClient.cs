using Castar.BL;
using Castar.Domain.Models.DataBase;
using Castar.Services.TelegramBot.Attributes;
using Castar.Services.TelegramBot.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;

namespace Castar.Services.TelegramBot
{
    public class BaseCastarTelegramBotClient: CommandHandler
    {
        protected Task PollingErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var ErrorMessage = exception switch
            {
                ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.WriteLine(ErrorMessage);
            return Task.CompletedTask;
        }

        protected async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            var handler = update.Type switch
            {
                // UpdateType.Unknown:
                // UpdateType.ChannelPost:
                // UpdateType.EditedChannelPost:
                // UpdateType.ShippingQuery:
                // UpdateType.PreCheckoutQuery:
                // UpdateType.Poll:
                UpdateType.Message => BotOnMessageReceived(botClient, update.Message!),
                UpdateType.EditedMessage => BotOnMessageReceived(botClient, update.EditedMessage!),
                //UpdateType.CallbackQuery:
                //UpdateType.InlineQuery:
                //UpdateType.ChosenInlineResult:
                _ => UnknownUpdateHandlerAsync(botClient, update)
            };

            try
            {
                await handler;
            }
#pragma warning disable CA1031
            catch (Exception exception)
#pragma warning restore CA1031
            {
                await PollingErrorHandler(botClient, exception, cancellationToken);
            }
        }

        private async Task BotOnMessageReceived(ITelegramBotClient botClient, Message message)
        {
            try
            {
                if (message.Type != MessageType.Text && message.Type != MessageType.Photo) return;
                var user = await usersService.GetUserByTelegramId(message.From.Id);
                if (user == null)
                {
                    await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                                text: $"❗️Пользователя с таким Id не существует. (`{message.From.Id}`)",
                                                                ParseMode.Markdown,
                                                                replyMarkup: new ReplyKeyboardRemove()
                                                                );
                    return;
                }
                var messageText = message.Text;
                var command = messageText?.Split(' ')[0];

                var sendmeCommand = GetCommandOfMethod(nameof(SendMe));
                var incomeCommand = GetCommandOfMethod(nameof(Income));
                if (command == sendmeCommand.Key)
                {
                    await SendMe(botClient, message);
                }
                else if (command == incomeCommand.Key)
                {
                    await Income(botClient, message);
                }
                else if ((messageText != null && messageText.StartsWith("t=")) || message.Photo != null)
                {
                    await AutoPurchase(botClient, message);
                }
                else
                {
                    await Purchase(botClient, message);
                }
            }
            catch (Exception ex)
            {
                await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                            text: $"{ex.Message} / {ex.InnerException?.Message}",
                                                            replyMarkup: new ReplyKeyboardRemove());
            }
        }
        private Task UnknownUpdateHandlerAsync(ITelegramBotClient botClient, Update update)
        {
            return Task.CompletedTask;
        }
        private KeyValuePair<string,string> GetCommandOfMethod(string typeMethodName)
        {
            var customAttr = typeof(CommandHandler)?.GetMethod(typeMethodName, BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetCustomAttributes(typeof(BotCommandAttribute), false);
            if (customAttr != null && customAttr.Length > 0)
            {
                var obj = customAttr[0] as BotCommandAttribute;
                if (obj != null)
                    return new(obj.command,obj.description);
            }
            return new();
        }
    }
}
