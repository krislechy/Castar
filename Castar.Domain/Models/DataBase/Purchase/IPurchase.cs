using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Domain.Models.DataBase
{
    public interface IPurchase
    {
        public string Name { get; set; }
        public long MessageId { get; set; }
        public decimal TotalSum { get; set; }
        public string RetailPlace { get; set; }
        public string RetailPlaceAddress { get; set; }
        public byte[]? RawData { get; set; }
        public ObservableCollection<Item> Items { get; set; }
        public DateTime? AddedOn { get; set; }
    }
}
