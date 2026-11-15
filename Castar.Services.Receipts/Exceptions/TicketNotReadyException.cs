using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts.Exceptions
{
    public sealed class TicketNotReadyException : Exception
    {
        public TicketNotReadyException(int statusCode, string message) : base(message) => StatusCode = statusCode;

        public int StatusCode { get; }
    }
}
