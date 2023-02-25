using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Domain.Models.DataBase
{
    [Table("Users")]
    public class User : BaseDbModel
    {
        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public Int64 TelegramId { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public string Name { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public int Sex { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        [Column("Color")]
        public byte[] ColorRaw { get; set; }=new byte[4];
        [NotMapped]
        public Color @Color
        {
            get
            {
                if (ColorRaw == null) return Color.White;
                return System.Drawing.Color.FromArgb(ColorRaw[0], ColorRaw[1], ColorRaw[2], ColorRaw[3]);
            }
            set
            {
                var bytes = new byte[4];
                bytes[0] = value.A;
                bytes[1] = value.R;
                bytes[2] = value.G;
                bytes[3] = value.B;
                ColorRaw = bytes;
            }
        }


        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        [Column("Image")]
        public byte[]? ImageRaw { get; set; }
        [NotMapped]
        public Image? Image
        {
            get {
                if (ImageRaw == null) return null;
                using var stream = new MemoryStream();
                stream.Write(ImageRaw, 0, ImageRaw.Length);
                return Image.FromStream(stream);
            }
            set
            {
                using var stream = new MemoryStream();
                value.Save(stream, value.RawFormat);
                ImageRaw = stream.ToArray();
            }
        }


        [NotMapped]
        public ObservableCollection<Item> Items { get; set; } = new ObservableCollection<Item>();
    }
}
