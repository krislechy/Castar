using System.Collections.ObjectModel;
using System.Drawing;

namespace Castar.Domain.Models.DataBase
{
    public class User : BaseDbModel
    {
        public long TelegramId { get; set; }
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

        public Image? Image
        {
            get
            {
                if (ImageRaw is null)
                    return null;

                using var stream = new MemoryStream(ImageRaw);
                using var image = Image.FromStream(stream);
                return (Image)image.Clone();
            }
            set
            {
                if (value is null)
                {
                    ImageRaw = null;
                    return;
                }

                using var stream = new MemoryStream();
                value.Save(stream, value.RawFormat);
                ImageRaw = stream.ToArray();
            }
        }

        public ObservableCollection<Item> Items { get; set; } = new();
    }
}
