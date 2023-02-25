using System.Globalization;
using System.Text.RegularExpressions;

namespace Castar.Extensions
{
    public static class StringExt
    {
        public static decimal? ToDecimal(this string value)
        {
            value = value.Replace(",", ".");
            var isDecimal = decimal.TryParse(value, NumberStyles.Currency, CultureInfo.InvariantCulture, out var @decimal);
            if (isDecimal)
            {
                return @decimal;
            }
            return null;
        }
        public static string RemoveMultipleSpaces(this string value)
        {
            return Regex.Replace(value, @"\s+", " ");
        }
    }
}
