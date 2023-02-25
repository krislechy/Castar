using Castar.BL;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using Castar.Extensions;
using Castar.Services.TelegramBot.Attributes;
using System;
using System.Collections.Generic;
using System.Globalization;
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
        [BotCommandAttribute("/income", "добавить начисление")]
        protected async Task<Message> Income(ITelegramBotClient botClient, Message message)
        {
            return await SendMessageAsync(botClient, message, async () =>
            {
                try
                {
                    var needUpdates = await incomesService.GetEntityByMessageId(message.MessageId);
                    if (needUpdates.Any())
                    {
                        string result = default!;
                        var messagesCount = GetIncomeObjectsFromText(message).Count();
                        var probablyUpdateCount = needUpdates.Count();
                        if (messagesCount > probablyUpdateCount)
                        {
                            int count = 0;
                            foreach (var s in GetIncomeObjectsFromText(message))
                            {
                                if (count < probablyUpdateCount)
                                {
                                    var el = needUpdates.ElementAt(count);
                                    result += await UpdateIncome(el, s.sum, s.source);
                                    count++;
                                }
                                else
                                {
                                    result += await CreateIncome(message, s.sum, s.source);
                                }
                            }
                        }
                        else if (messagesCount < probablyUpdateCount)
                        {
                            int count = 0;
                            foreach (var s in GetIncomeObjectsFromText(message))
                            {
                                if (count < probablyUpdateCount)
                                {
                                    var el = needUpdates.ElementAt(count);
                                    result += await UpdateIncome(el,s.sum,s.source);
                                    count++;
                                }
                            }
                            foreach (var s in needUpdates.Skip(count))
                            {
                                result += await DeleteIncome(s.Id,s.Sum,s.Source);
                            }
                        }
                        else
                        {
                            int count = 0;
                            foreach (var s in GetIncomeObjectsFromText(message))
                            {
                                var el = needUpdates.ElementAt(count);
                                result += await UpdateIncome(el, s.sum, s.source);
                                count++;
                            }
                        }
                        return result;
                    }
                    else
                    {
                        string result = default!;
                        foreach (var s in GetIncomeObjectsFromText(message))
                        {
                            result += await CreateIncome(message, s.sum, s.source);
                        }
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    throw;
                }
            });
        }
        private IEnumerable<(string source, decimal sum)> GetIncomeObjectsFromText(Message message)
        {
            var messages = message.Text.Split("\n");
            foreach (var mes in messages)
            {
                bool isValid = false;
                var _message = mes.RemoveMultipleSpaces();
                var splitted = _message.Split(' ');
                if (splitted.Length == 3)
                {
                    var sum = splitted[2].Trim().ToDecimal();
                    if (sum != null)
                    {
                        var comment = splitted[1].Trim();
                        isValid = true;
                        yield return (comment, sum.Value);
                    }
                }
                if (!isValid)
                    throw new Exception($"❗️Требуемый формат: /income [источник] [сумма]");
            }
        }
        private async Task<string> UpdateIncome(Income income, decimal sum, string source)
        {
            if (sum < 0)
            {
                return await DeleteIncome(income.Id, income.Sum, income.Source);
            }
            else
            {
                income.Sum = sum;
                income.Source = source;
                await incomesService!.Update(income);
                return $"{settings.TelegramBot.IncomeUpdateEmoji} %user% обновил(-а) начисление:\n`{sum.ToCurrency()}, {source}`\n";
            }
        }
        private async Task<string> DeleteIncome(Guid id, decimal sum, string source)
        {
            await incomesService!.Delete(id);
            return $"{settings.TelegramBot.IncomeDeleteEmoji} %user% удалил(-а) начисление:\n`{sum.ToCurrency()}, {source}`\n";
        }
        private async Task<string> CreateIncome(Message message, decimal sum, string source)
        {
            IIncome income = new Income();
            income.Sum = sum;
            income.Source = source;
            income.MessageId = message.MessageId;
            await incomesService!.Add((Income)income, message!.From!.Id);
            return $"{settings.TelegramBot.IncomeCreateEmoji} %user% добавил(-а) начисление:\n`{sum.ToCurrency()}, {source}`\n";
        }
    }
}
