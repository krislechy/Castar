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
    [Table("Items")]
    public class Item : BaseDbModel, IItem
    {
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public Purchase? Purchase { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public Guid PurchaseId { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public string Name { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public int? Category { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public double Quantity { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.None), Required]
        public decimal Price { get; set; }


        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public decimal Sum { get; set; }

        [NotMapped]
        public ObservableCollection<User> Users { get; set; } = new ObservableCollection<User>();
    }
}
