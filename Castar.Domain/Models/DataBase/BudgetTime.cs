namespace Castar.Domain.Models.DataBase;

public static class BudgetTime
{
    public static TimeZoneInfo Belgrade { get; } = Resolve("Europe/Belgrade", "Central Europe Standard Time");

    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime InZone(DateTime utc, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), zone), DateTimeKind.Unspecified);

    public static DateTime FromLocal(DateTime value, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime;
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static DateOnly BelgradeDate(DateTime utc) => DateOnly.FromDateTime(InZone(utc, Belgrade));

    public static TimeZoneInfo Resolve(string ianaId, string windowsId) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var zone) ? zone : TimeZoneInfo.FindSystemTimeZoneById(windowsId);
}
