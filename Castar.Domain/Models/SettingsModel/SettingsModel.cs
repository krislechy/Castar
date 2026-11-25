using System.Xml.Serialization;

namespace Castar.Domain.Models.SettingsModel
{
    public class SettingsModel
    {
        private ReceiptSettings receipts = new();
        private NalogFNS nalogFNS = new();
        private ManualCurrencySettings manualCurrencies = ManualCurrencySettings.Default;

        public Systems Systems { get; set; } = new();
        public TelegramBot TelegramBot { get; set; } = new();
        public NalogFNS NalogFNS
        {
            get => Volatile.Read(ref nalogFNS);
            set => Volatile.Write(ref nalogFNS, value);
        }
        public ReceiptSettings Receipts
        {
            get => Volatile.Read(ref receipts);
            set => Volatile.Write(ref receipts, value);
        }

        [XmlIgnore]
        public ManualCurrencySettings ManualCurrencies
        {
            get => Volatile.Read(ref manualCurrencies);
            set => Volatile.Write(ref manualCurrencies, value);
        }
    }

    public sealed class ReceiptSettings : BaseSettingsModel
    {
        public const int DefaultRetryMinutes = 5;
        public const int MaxRetryMinutes = 1440;

        private bool isEnabled = true;
        private string provider = "serbia";

        public int RetryMinutes { get; init; } = DefaultRetryMinutes;

        public static bool IsValidRetryMinutes(int minutes) => minutes is >= 1 and <= MaxRetryMinutes;

        public bool IsEnabled
        {
            get => isEnabled;
            set => SetField(ref isEnabled, value);
        }

        public string Provider
        {
            get => provider;
            set => SetField(ref provider, value);
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
    public sealed class TelegramBot : BaseSettingsModel
    {
        private long testChatId;

        [XmlIgnore]
        public long? TestChatId
        {
            get
            {
                var value = Interlocked.Read(ref testChatId);
                return value == 0 ? null : value;
            }
            set
            {
                var next = value.GetValueOrDefault();
                if (Interlocked.Exchange(ref testChatId, next) != next)
                    OnPropertyChanged(nameof(TestChatId));
            }
        }

        [XmlIgnore]
        private string? telegramBotApi_KEY;
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
        public string? INN
        {
            get => this.inn;
            set => SetField(ref inn, value);
        }
        [XmlIgnore]
        private string? password;
        public string? Password
        {
            get => this.password;
            set => SetField(ref password, value);
        }
    }
}
