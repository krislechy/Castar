using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Extensions
{
    public static class DecimalExt
    {
        public static string ToCurrency(this decimal value)
        {
            return string.Format("{0:C2}", value);
        }
    }
}
