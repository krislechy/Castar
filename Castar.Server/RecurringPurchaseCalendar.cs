using Castar.Api;
using Castar.Domain.Models.DataBase;

namespace Castar.Server;

internal static class RecurringPurchaseCalendar
{
    internal static DateTime? GetUtc(DateTime anchor, RecurringPurchaseFrequency frequency,
        TimeZoneInfo zone, int occurrence)
    {
        if (occurrence < 0)
            return null;
        try
        {
            var local = frequency switch
            {
                RecurringPurchaseFrequency.Weekly => anchor.AddDays((long)occurrence * 7),
                RecurringPurchaseFrequency.Monthly => anchor.AddMonths(occurrence),
                RecurringPurchaseFrequency.Yearly => anchor.AddYears(occurrence),
                _ => throw new ArgumentOutOfRangeException(nameof(frequency))
            };
            // Keep the original anchor for the next payment after a shorter month or a clock change.
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(local))
                local = local.AddMinutes(1);
            return BudgetTime.FromLocal(local, zone);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static int FirstAfter(DateTime anchor, RecurringPurchaseFrequency frequency, TimeZoneInfo zone,
        DateTime nowUtc, int minimum)
    {
        var now = BudgetTime.InZone(nowUtc, zone);
        var estimate = frequency switch
        {
            RecurringPurchaseFrequency.Weekly => (now.Date - anchor.Date).Days / 7,
            RecurringPurchaseFrequency.Monthly => (now.Year - anchor.Year) * 12 + now.Month - anchor.Month,
            RecurringPurchaseFrequency.Yearly => now.Year - anchor.Year,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
        var occurrence = Math.Max(minimum, estimate - 1);
        while (GetUtc(anchor, frequency, zone, occurrence) is { } due && due <= nowUtc)
            occurrence++;
        return occurrence;
    }
}
