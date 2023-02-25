using Castar.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Castar.Domain.Models.SettingsModel
{
    public class SettingsModel
    {
        public Systems Systems { get; set; } = new();
        public DataBase DataBase { get; set; } = new();
        public TelegramBot TelegramBot { get; set; } = new();
        public NalogFNS NalogFNS { get; set; } = new();
        public void SetHandlersEvents(Action handler)
        {
            //var properties = typeof(SettingsModel).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            //if (properties != null)
            //{
            //    foreach (var property in properties)
            //    {
            //        var currentObj = property.GetValue(this);
            //        var eventInfo = (EventInfo)currentObj.GetType().GetEvent("PropertyChanged");
            //        var handler = Delegate.CreateDelegate(hanlder.GetType(),
            //                        this,
            //                         eventInfo.AddMethod);
            //        eventInfo?.AddEventHandler(currentObj, handler);
            //    }
            //}
            this.Systems.PropertyChanged += (q, e) => handler();
            this.DataBase.PropertyChanged += (q, e) => handler();
            this.TelegramBot.PropertyChanged += (q, e) => handler();
            this.NalogFNS.PropertyChanged += (q, e) => handler();
        }
        public SettingsModel Copy()
        {
            var xmlSerializer = new XmlSerializer(typeof(SettingsModel));
            using (var streamWrite = new MemoryStream())
            {
                xmlSerializer.Serialize(streamWrite, this);

                using (var streamRead = new MemoryStream(streamWrite.GetBuffer()))
                {
                    if (streamRead.Length != 0)
                        return xmlSerializer.Deserialize(streamRead) as SettingsModel;
                }
            }
            return null;
        }
    }
    [Serializable]
    [XmlSerializerAssembly("Castar.Domain.XmlSerializers")]
    public sealed class Systems : BaseSettingsModel
    {
        [XmlIgnore]
        private bool hideInTray = false;
        public bool IsHideInTray
        {
            get => this.hideInTray;
            set => SetField(ref hideInTray, value);
        }
        [XmlIgnore]
        private bool isAutoRun = false;
        public bool IsAutoRun
        {
            get => this.isAutoRun;
            set => SetField(ref isAutoRun, value);
        }
        [XmlIgnore]
        private bool isEnableTelegramBot = false;
        public bool IsEnableTelegramBot
        {
            get => this.isEnableTelegramBot;
            set => SetField(ref isEnableTelegramBot, value);
        }
        [XmlIgnore]
        private bool isEnableNalogFNS = false;
        public bool IsEnableNalogFNS
        {
            get => this.isEnableNalogFNS;
            set => SetField(ref isEnableNalogFNS, value);
        }
    }
    [Serializable]
    [XmlSerializerAssembly("Castar.Domain.XmlSerializers")]
    public sealed class DataBase : BaseSettingsModel
    {
        [XmlIgnore]
        private string dataSource = @".\SQLEXPRESS";
        public string DataSource
        {
            get => this.dataSource;
            set => SetField(ref dataSource, value);
        }
        [XmlIgnore]
        private string pathToDataBase = "";
        public string AttachDBFileName
        {
            get => this.pathToDataBase;
            set => SetField(ref pathToDataBase, value);
        }
        [XmlIgnore]
        private string login = "";
        [Encryption]
        public string Login
        {
            get => this.login;
            set => SetField(ref login, value);
        }
        [XmlIgnore]
        private string? password;
        [Encryption]
        public string? Password
        {
            get => this.password;
            set => SetField(ref password, value);
        }
    }
    [Serializable]
    [XmlSerializerAssembly("Castar.Domain.XmlSerializers")]
    public sealed class TelegramBot : BaseSettingsModel
    {
        [XmlIgnore]
        private string? telegramBotApi_KEY;
        [Encryption]
        public string? TelegramBotApi_KEY
        {
            get => this.telegramBotApi_KEY;
            set => SetField(ref telegramBotApi_KEY, value);
        }
        [XmlIgnore]
        private string? status;
        [XmlIgnore]
        public string? Status
        {
            get => this.status;
            set => SetField(ref status, value);
        }
        [XmlIgnore]
        private string? incomeCreateEmoji = "⭐️";
        public string? IncomeCreateEmoji
        {
            get => this.incomeCreateEmoji;
            set => SetField(ref incomeCreateEmoji, value);
        }
        [XmlIgnore]
        private string? incomeUpdateEmoji = "♻️";
        public string? IncomeUpdateEmoji
        {
            get => this.incomeUpdateEmoji;
            set => SetField(ref incomeUpdateEmoji, value);
        }
        [XmlIgnore]
        private string? incomeDeleteEmoji = "❌";
        public string? IncomeDeleteEmoji
        {
            get => this.incomeDeleteEmoji;
            set => SetField(ref incomeDeleteEmoji, value);
        }
        [XmlIgnore]
        private string? purchaseCreateEmoji = "🔸";
        public string? PurchaseCreateEmoji
        {
            get => this.purchaseCreateEmoji;
            set => SetField(ref purchaseCreateEmoji, value);
        }
        [XmlIgnore]
        private string? purchaseUpdateEmoji = "♻️";
        public string? PurchaseUpdateEmoji
        {
            get => this.purchaseUpdateEmoji;
            set => SetField(ref purchaseUpdateEmoji, value);
        }
        [XmlIgnore]
        private string? purchaseDeleteEmoji = "❌";
        public string? PurchaseDeleteEmoji
        {
            get => this.purchaseDeleteEmoji;
            set => SetField(ref purchaseDeleteEmoji, value);
        }
        [XmlIgnore]
        private string? itemEmoji = "🔸";
        public string? ItemEmoji
        {
            get => this.itemEmoji;
            set => SetField(ref itemEmoji, value);
        }
    }

    [Serializable]
    [XmlSerializerAssembly("Castar.Domain.XmlSerializers")]
    public sealed class NalogFNS : BaseSettingsModel
    {
        [XmlIgnore]
        private string? inn;
        [Encryption]
        public string? INN
        {
            get => this.inn;
            set => SetField(ref inn, value);
        }
        [XmlIgnore]
        private string? password;
        [Encryption]
        public string? Password
        {
            get => this.password;
            set => SetField(ref password, value);
        }
    }
}
