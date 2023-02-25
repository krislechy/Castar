using Castar.Services.TelegramBot.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Castar.Services.TelegramBot.Commands
{
    public partial class CommandHandler
    {
        [BotCommandAttribute("/me", "получить свой идентификатор")]
        protected async Task<Message> SendMe(ITelegramBotClient botClient, Message message)
        {
            return await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                                        text: $"{message?.From?.Username ?? "Unknowed"}:{message?.From?.Id ?? -1}",
                                                        replyMarkup: new ReplyKeyboardRemove());
        }
    }
}
