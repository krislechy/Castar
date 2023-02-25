using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Models
{
    public sealed class TicketId
    {
        public string kind { get; set; }
        public string id { get; set; }
        public int status { get; set; }
        public int statusReal { get; set; }
    }
}
