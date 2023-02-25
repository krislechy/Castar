using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Models
{
    public sealed class RefreshToken
    {
        public string sessionId { get; set; }
        public string refresh_token { get; set; }
    }
}
