using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Models
{
    public sealed class Authenticate
    {
        public string sessionId { get; set; }
        public string refresh_token { get; set; }
        public string phone { get; set; }
        public string name { get; set; }
        public string email { get; set; }
        public string surname { get; set; }
    }
}
