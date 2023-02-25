using Castar.BL;
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
        public async Task<Message> SendMessageAsync(ITelegramBotClient botClient, Message message, Func<Task<string>> hanlde)
        {
            await botClient.SendChatActionAsync(message.Chat.Id, Telegram.Bot.Types.Enums.ChatAction.Typing);
            var task = hanlde.Invoke();
            do
            {
                await botClient.SendChatActionAsync(message.Chat.Id, Telegram.Bot.Types.Enums.ChatAction.Typing);
                await Task.Delay(300);
            } while (!task.IsCompleted);
            if (task.IsCompletedSuccessfully)
            {
                var users = await usersService.GetAllUsersWithTelegramId();
                var initialUser = users.First(x => x.TelegramId == message.From.Id);
                foreach (var user in users)
                {
#if(DEBUG)
                    if (user.TelegramId != 183066481) continue;
#endif
                    message = await botClient.SendTextMessageAsync(chatId: user.TelegramId,
                                                        text: task.Result.Replace("%user%", initialUser.Name),
                                                        parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                                                        replyMarkup: new ReplyKeyboardRemove()
                                                        );
                }
            }
            else
                message = await botClient.SendTextMessageAsync(chatId: message.Chat.Id,
                                text: task.Exception.InnerException.Message,
                                parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                                replyMarkup: new ReplyKeyboardRemove()
                                );
            return message;
        }
    }
}
