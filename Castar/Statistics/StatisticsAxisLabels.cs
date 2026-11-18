using System;
using System.Globalization;

namespace Castar.Statistics;

internal static class StatisticsAxisLabels
{
    internal static string Amount(double value)
    {
        var magnitude = Math.Abs(value);
        var culture = CultureInfo.CurrentCulture;
        if (magnitude >= 1_000_000)
            return (value / 1_000_000).ToString("0.##", culture) + "M";
        if (magnitude >= 1_000)
            return (value / 1_000).ToString("0.##", culture) + "k";
        return value.ToString("0.####", culture);
    }
}
