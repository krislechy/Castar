using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Models
{
    public sealed class Ticket
    {
        public int status { get; set; }
        public int statusReal { get; set; }
        public string id { get; set; }
        public string kind { get; set; }
        public DateTime createdAt { get; set; }
        public StatusDescription statusDescription { get; set; }
        public string qr { get; set; }
        public Operation operation { get; set; }
        public List<Process> process { get; set; }
        public Query query { get; set; }
        public _Ticket ticket { get; set; }
        public Organization organization { get; set; }
        public Seller seller { get; set; }
    }
}
