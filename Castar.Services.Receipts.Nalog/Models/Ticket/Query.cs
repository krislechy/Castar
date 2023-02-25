using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Models
{
    public class Query
    {
        public int operationType { get; set; }
        public int sum { get; set; }
        public int documentId { get; set; }
        public string fsId { get; set; }
        public string fiscalSign { get; set; }
        public string date { get; set; }
    }
}
