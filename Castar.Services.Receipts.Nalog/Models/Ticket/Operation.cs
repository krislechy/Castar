using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Models
{
    public sealed class Operation
    {
        public string date { get; set; }
        public int type { get; set; }
        public int sum { get; set; }
    }
}
