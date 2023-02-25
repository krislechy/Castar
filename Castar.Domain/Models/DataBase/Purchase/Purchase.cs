using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Domain.Models.DataBase
{
    [Table("Purchases")]
    public class Purchase : BaseDbModel, IPurchase
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public User User { get; private set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public Guid UserId { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public string Name { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public decimal TotalSum { get; set; }

        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long MessageId { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public string? RetailPlace { get; set; }

        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public string? RetailPlaceAddress { get; set; }

        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public byte[]? RawData { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public ObservableCollection<Item> Items { get; set; } = new ObservableCollection<Item>();


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public DateTime? AddedOn { get; set; }
    }
}
