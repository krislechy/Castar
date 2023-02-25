using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Domain.Models.DataBase
{
    public interface IItem
    {
        Guid PurchaseId { get; set; }
        string Name { get; set; }
        public double Quantity { get; set; }
        public decimal Price { get; set; }
    }
}
