using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Domain.Models.DataBase
{
    public interface IIncome
    {
        public decimal Sum { get; set; }
        public string Source { get; set; }
        public long MessageId { get; set; }
    }
}
