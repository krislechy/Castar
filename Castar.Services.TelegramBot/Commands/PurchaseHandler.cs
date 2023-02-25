using Castar.BL;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using Castar.Extensions;
using Castar.Services.TelegramBot.Attributes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        [BotCommandAttribute("Ручное добавление покупки", "[наименование товра] [сумма]")]
        protected async Task<Message> Purchase(ITelegramBotClient botClient, Message message)
        {
            return await SendMessageAsync(botClient, message, async () =>
            {
                try
                {
                    var pur_messages = GetItemObjectsFromText(message);
                    var messagesCount = pur_messages.Count();
                    string result = default!;
                    var needUpdates = (await purchasesService.GetEntityByMessageId(message.MessageId)).FirstOrDefault();
                    if (needUpdates != default)
                    {
                        var probablyUpdateCount = needUpdates.Items.Count();
                        var purchaseId = needUpdates.Id;
                        if (messagesCount > probablyUpdateCount)
                        {
                            int count = 0;
                            foreach (var s in pur_messages)
                            {
                                if (count < probablyUpdateCount)
                                {
                                    var el = needUpdates.Items.ElementAt(count);
                                    var updatedItem = await UpdateItemLocal(el, s.name, s.sum);
                                    el = updatedItem.item;
                                    result += updatedItem.message;
                                    count++;
                                }
                                else
                                {
                                    var createdItem = await CreateItemLocal(message, purchaseId, s.name, s.sum);
                                    needUpdates.Items.Add(createdItem.item);
                                    result += createdItem.message;
                                }
                            }
                        }
                        else if (messagesCount < probablyUpdateCount)
                        {
                            int count = 0;
                            foreach (var s in pur_messages)
                            {
                                if (count < probablyUpdateCount)
                                {
                                    var el = needUpdates.Items.ElementAt(count);
                                    var updatedItem = await UpdateItemLocal(el, s.name, s.sum);
                                    el = updatedItem.item;
                                    result += updatedItem.message;
                                    count++;
                                }
                            }
                            foreach (var s in needUpdates.Items.Skip(count))
                            {
                                result += await DeleteItem(needUpdates.Items, s, s.Sum, s.Name);
                            }
                        }
                        else
                        {
                            int count = 0;
                            if (pur_messages.Any(x => x.sum < 0))
                            {
                                await purchasesService.Delete(needUpdates.Id);
                                return $"{settings.TelegramBot.PurchaseDeleteEmoji} %user% удалил(а) покупку [{needUpdates.Name}] - `{needUpdates.Items.Sum(c => c.Sum).ToCurrency()}`";
                            }
                            else
                            {
                                foreach (var s in pur_messages)
                                {
                                    var el = needUpdates.Items.ElementAt(count);
                                    var updatedItem = await UpdateItemLocal(el, s.name, s.sum);
                                    el = updatedItem.item;
                                    result += updatedItem.message;
                                    count++;
                                }
                            }
                        }
                        await purchasesService.Update(needUpdates);
                        return result;
                    }
                    else
                    {
                        int count = 0;
                        var purchase = await CreatePurchaseLocal(message, messagesCount, pur_messages.First().name);
                        foreach (var s in pur_messages)
                        {
                            var createdItem = await CreateItemLocal(message, new Guid(), s.name, s.sum);
                            purchase.Items.Add(createdItem.item);
                            result += createdItem.message;
                            count++;
                        }
                        await purchasesService.Add(purchase, message!.From!.Id);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    throw;
                }
            });
        }
        private IEnumerable<(string name, decimal sum)> GetItemObjectsFromText(Message message)
        {
            List<(string, decimal)> list = new List<(string, decimal)>();
            var messages = message!.Text!.Split("\n");
            foreach (var mes in messages)
            {
                bool isValid = false;
                var _message = mes.RemoveMultipleSpaces();
                var splitted = _message.Split(' ');
                if (splitted.Length > 1)
                {
                    var sum = splitted.Last().Trim().ToDecimal();
                    if (sum != null)
                    {
                        var name = String.Join(" ", splitted.SkipLast(1)).Trim();
                        isValid = true;
                        list.Add((name, sum.Value));
                    }
                }
                if (!isValid)
                    throw new Exception($"❗️Требуемый формат для покупок: [наименование товара] [сумма]");
            }
            return list;
        }
        private Task<(Item item, string message)> UpdateItemLocal(Item item, string name, decimal sum)
        {
            item.Price = sum;
            item.Sum = sum;
            item.Name = name;
            return Task.FromResult((item, $"{settings.TelegramBot.PurchaseUpdateEmoji} %user% обновил(а) товар [{name}] - `{sum.ToCurrency()}`\n"));
        }
        private async Task<string> DeleteItem(ObservableCollection<Item> items,Item item, decimal sum, string name)
        {
            items.Remove(item);
            await itemsService.Delete(item.Id);
            return $"{settings.TelegramBot.PurchaseDeleteEmoji} %user% удалил(а) товар [{name}] - `{sum.ToCurrency()}`\n";
        }
        private Task<(Item item, string message)> CreateItemLocal(Message message, Guid purchaseId, string name, decimal sum)
        {
            var item = new Item();
            item.Name = name;
            item.Price = sum;
            item.Quantity = 1;
            item.Sum = sum;
            item.PurchaseId = purchaseId;
            return Task.FromResult((item, $"{settings.TelegramBot.PurchaseCreateEmoji} %user% добавил(а) товар [{item.Name}] - `{item.Sum.ToCurrency()}`\n"));
        }
        private Task<Purchase> CreatePurchaseLocal(Message message, int messagesCount, string name)
        {
            var purchase = new Purchase();
            purchase.MessageId = message.MessageId;
            purchase.Name = messagesCount == 1 ? name : "№" + message.MessageId.ToString();
            return Task.FromResult(purchase);
        }
    }
}
