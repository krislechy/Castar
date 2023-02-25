using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Exceptions
{
    public sealed class TicketNotReadyException:Exception
    {
        public int StatusCode { get; set; }
        public string Message { get; set; }
        public TicketNotReadyException(int statusCode, string Message)
        {
            this.StatusCode = statusCode;
            this.Message = Message;
        }
    }
}
