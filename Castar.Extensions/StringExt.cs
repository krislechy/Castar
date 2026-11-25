using System.Globalization;

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
    }
}
