using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Models
{
    public sealed class Item
    {
        public string name { get; set; }
        public int nds { get; set; }
        public int ndsSum { get; set; }
        public int paymentType { get; set; }
        public int price { get; set; }
        public decimal priceDecimal { get => ((decimal)price / 100); }
        public int productType { get; set; }
        public double quantity { get; set; }
        public int sum { get; set; }
        public decimal sumDecimal { get => ((decimal)sum / 100); }
    }
}
