using Castar.Compontents;
using Castar.Currency;
using Castar.Domain.Models.SettingsModel;
using System;

namespace Castar.Contents;

internal class PeriodComparison(bool increaseIsFavorable = true) : BaseSettingsModel
{
    private bool isComparisonLoading;
    private decimal? comparison;
    private string comparisonText = "—";
    private string comparisonHint = "К предыдущему периоду";

    public bool IsComparisonLoading { get => isComparisonLoading; set => SetField(ref isComparisonLoading, value); }
    public string ComparisonText { get => comparisonText; private set => SetField(ref comparisonText, value); }
    public string ComparisonHint { get => comparisonHint; private set => SetField(ref comparisonHint, value); }
    public bool ComparisonAvailable => comparison.HasValue;
    public bool IsComparisonFavorable => increaseIsFavorable ? comparison > 0 : comparison < 0;
    public bool IsComparisonUnfavorable => increaseIsFavorable ? comparison < 0 : comparison > 0;
    internal void SetComparison(decimal? value, string currency, string hint = "К предыдущему периоду")
    {
        comparison = value;
        ComparisonText = value is { } amount ? (amount > 0 ? "+" : "") + HistoricalExchangeRates.Format(amount, currency) : "—";
        ComparisonHint = hint;
        OnPropertyChanged(nameof(ComparisonAvailable));
        OnPropertyChanged(nameof(IsComparisonFavorable));
        OnPropertyChanged(nameof(IsComparisonUnfavorable));
    }

    internal static RangeDate? PreviousPeriod(DateTime start, DateTime finish)
    {
        start = start.Date;
        finish = finish.Date;
        if (start > finish || start == DateTime.MinValue)
            return null;
        var months = (finish.Year - start.Year) * 12 + finish.Month - start.Month + 1;
        if (start.Day == 1 && finish.Day == DateTime.DaysInMonth(finish.Year, finish.Month))
        {
            var monthsSinceMinimum = (start.Year - 1) * 12 + start.Month - 1;
            return months > monthsSinceMinimum ? null : new RangeDate(start.AddMonths(-months), start.AddDays(-1));
        }
        var days = (finish - start).Days + 1;
        return start.Ticks / TimeSpan.TicksPerDay < days ? null : new RangeDate(start.AddDays(-days), start.AddDays(-1));
    }
}
