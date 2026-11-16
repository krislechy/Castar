using System.Collections.ObjectModel;
using System.Drawing;

namespace Castar.Domain.Models.DataBase
{
    public class User : BaseDbModel
    {
        public long TelegramId { get; set; }
        public bool TelegramNotificationsEnabled { get; set; } = true;
        public string Name { get; set; } = string.Empty;
        public int Sex { get; set; }
        public byte[] ColorRaw { get; set; } = new byte[4];

        public Color Color
        {
            get => ColorRaw.Length == 4
                ? Color.FromArgb(ColorRaw[0], ColorRaw[1], ColorRaw[2], ColorRaw[3])
                : Color.White;
            set => ColorRaw = [value.A, value.R, value.G, value.B];
        }

        public byte[]? ImageRaw { get; set; }

        public ObservableCollection<Item> Items { get; set; } = new();
    }
}
