using System.Diagnostics.CodeAnalysis;

namespace Castar.Api;

public static partial class RecurringPurchaseTime
{
    public const int MaxOffsetMinutes = 14 * 60;

    public static bool TryResolve(RecurringPurchaseWriteDto schedule, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        if (schedule.ZoneRuleId is { } ruleId)
            return RulesById.TryGetValue(ruleId, out var rule) && rule.TryResolve(out zone);

        if (schedule.UtcOffsetMinutes is { } offset)
        {
            if (offset is < -MaxOffsetMinutes or > MaxOffsetMinutes)
                return false;

            var label = FormatOffset(offset);
            zone = TimeZoneInfo.CreateCustomTimeZone(label, TimeSpan.FromMinutes(offset), label, label);
            return true;
        }

        return !string.IsNullOrWhiteSpace(schedule.TimeZoneId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(schedule.TimeZoneId, out zone);
    }

    public static string FormatOffset(int minutes)
    {
        var offset = TimeSpan.FromMinutes(minutes).Duration();
        return FormattableString.Invariant($"UTC{(minutes < 0 ? '-' : '+')}{(int)offset.TotalHours:00}:{offset.Minutes:00}");
    }
}

public sealed record RecurringPurchaseZoneRule(int Id, string WindowsId, string IanaId)
{
    public bool TryResolve([NotNullWhen(true)] out TimeZoneInfo? zone) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(IanaId, out zone) ||
        TimeZoneInfo.TryFindSystemTimeZoneById(WindowsId, out zone);
}
