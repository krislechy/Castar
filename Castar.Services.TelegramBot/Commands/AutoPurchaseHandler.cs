using Castar.BL;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using Castar.Extensions;
using Castar.Services.Receipts.Models;
using Castar.Services.Receipts;
using Castar.Services.TelegramBot.Attributes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Castar.Services.Receipts.Barcode;
using ZXing;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;
using System.Net.Sockets;

namespace Castar.Services.TelegramBot.Commands
{
    public partial class CommandHandler
    {
        [BotCommandAttribute("Автоматическое добавление покупки", "t= || photo")]
        protected async Task<Message> AutoPurchase(ITelegramBotClient botClient, Message message)
        {
            return await SendMessageAsync(botClient, message, async () =>
            {
                if (!settings.Systems.IsEnableNalogFNS) return "❗️Сервис ФНС Проверка чека - отключен";
                try
                {
                    IReceipt<Ticket> nalog = new Nalog(new System.Net.NetworkCredential(settings.NalogFNS.INN,settings.NalogFNS.Password));
                    Task<string> action = message.Type switch
                    {
                        MessageType.Text => await Task.Factory.StartNew(async () =>
                        {
                            var purchase = new Purchase();
                            var queries = message!.Text!.Split("\n");
                            StringBuilder sb = new StringBuilder();
                            foreach (var query in queries)
                            {
                                try
                                {
                                    var text = await AddAutoPurchaseHandler(query, message, nalog);
                                    sb.AppendLine(text);
                                }
                                catch (Exception ex)
                                {
                                    return $"{ex.Message}\n{ex.InnerException?.Message}";
                                }
                            }
                            return sb.ToString();
                        })
                        ,
                        MessageType.Photo => await Task.Factory.StartNew(async () =>
                        {
                            StringBuilder sb = new StringBuilder();
                            await foreach (var query in getQueriesFromPhotos(botClient, message.Photo))
                            {
                                try
                                {
                                    var text=await AddAutoPurchaseHandler(query,message,nalog);
                                    sb.AppendLine(text);
                                }
                                catch (Exception ex)
                                {
                                    return $"{ex.Message}\n{ex.InnerException?.Message}";
                                }
                            }
                            return sb.ToString();
                        })
                        ,
                        _ => Task.FromResult("❗️Не поддерживаемое действие.")
                    };
                    return await action;
                }
                catch (Exception ex)
                {
                    throw;
                }
            });
        }
        private async Task<string> AddAutoPurchaseHandler(string query, Message message, IReceipt<Ticket> nalog)
        {
            query = query.Trim();
            if (await CheckExistPurchase(query.Trim()))
                throw new Exception($"❗️Такая покупка уже была добавлена ранее.\n`{query}`");
            var result = await nalog.GetReceipt(query);
            var text = await AddAutoPurchase(result, message!);
            return text;
        }
        private async Task<bool> CheckExistPurchase(string query)
        {
            return await purchasesService.IsExistTheSame(Encoding.UTF8.GetBytes(query));
        }
        private async Task<string> AddAutoPurchase(Ticket? ticket, Message message)
        {
            StringBuilder result = new StringBuilder();
            if (ticket != null)
            {
                IPurchase purchase = new Purchase();
                purchase.Name = ticket.ticket.document.receipt.user;
                purchase.RetailPlace = ticket.ticket.document.receipt.retailPlace;
                purchase.RetailPlaceAddress = ticket.ticket.document.receipt.retailPlaceAddress;
                purchase.RawData = Encoding.UTF8.GetBytes(ticket.qr);
                purchase.MessageId = message.MessageId;
                purchase.AddedOn = ticket.ticket.document.receipt.dateTime.UnixTimeStampToDateTime();
                result.AppendLine($"{settings.TelegramBot.PurchaseCreateEmoji} %user% добавил(а) покупку {purchase.Name}");
                result.AppendLine($"{purchase.RetailPlace} / {purchase.RetailPlaceAddress}");
                result.AppendLine($"Список товаров (`{ticket.ticket.document.receipt.items.Sum(x => x.sumDecimal).ToCurrency()}`):");
                foreach (var item in ticket.ticket.document.receipt.items)
                {
                    IItem ditem = new Domain.Models.DataBase.Item();
                    ditem.Price = item.priceDecimal;
                    ditem.Quantity = item.quantity;
                    ditem.Name = item.name;
                    ditem.PurchaseId = new Guid();
                    purchase.Items.Add((Domain.Models.DataBase.Item)ditem);

                    result.AppendLine($"{settings.TelegramBot.ItemEmoji} {item.name} (Кол-во: {item.quantity}) - `{item.sumDecimal.ToCurrency()}`");
                }

                await purchasesService.Add((Purchase)purchase, message.From.Id);
            }
            return result.ToString();
        }
        private async IAsyncEnumerable<string> getQueriesFromPhotos(ITelegramBotClient botClient,IEnumerable<PhotoSize> photosSize)
        {
            List<string> alreadyReturned = new List<string>();
            foreach(var photoSize in photosSize)
            {
                using (var ms = new MemoryStream())
                {
                    var photoObject = await botClient.GetInfoAndDownloadFileAsync(photoSize.FileId, ms);
#pragma warning disable CA1416 // Проверка совместимости платформы
                    var photo = Image.FromStream(ms);
                    var bitmap = new Bitmap(photo);
#pragma warning restore CA1416 // Проверка совместимости платформы
                    var decode = new QrCode().Decode(bitmap);
                    if (!alreadyReturned.Any(x => x == decode))
                    {
                        alreadyReturned.Add(decode);
                        yield return decode;
                    }
                }
            }
        }
    }
}
