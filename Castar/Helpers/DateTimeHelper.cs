using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Helpers
{
    internal class DateTimeHelper
    {
        public static IEnumerable<DateTime> EachMonth(DateTime from, DateTime thru)
        {
            for (var month = new DateTime(from.Date.Year, from.Date.Month, 1); month.Date <= thru.Date; month = month.AddMonths(1))
                yield return month;
        }
    }
}
