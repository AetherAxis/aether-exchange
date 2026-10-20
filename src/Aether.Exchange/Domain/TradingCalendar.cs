using System;

namespace Aether.Exchange;

/// <summary>交易所交易时段表（宽口径并集）。</summary>
public static class TradingCalendar
{
    public static bool IsTradingTime(DateTime now)
    {
        var dt = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
        var time = dt.TimeOfDay;
        var dow = dt.DayOfWeek;

        if (time >= TimeSpan.FromHours(21) && time <= TimeSpan.FromHours(23).Add(TimeSpan.FromMinutes(59.999)))
            return dow >= DayOfWeek.Monday && dow <= DayOfWeek.Friday;
        if (time >= TimeSpan.Zero && time < TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(30)))
            return dow >= DayOfWeek.Tuesday && dow <= DayOfWeek.Saturday;

        if (dow < DayOfWeek.Monday || dow > DayOfWeek.Friday)
            return false;
        if (time >= TimeSpan.FromHours(9) && time < TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(15)))
            return true;
        if (time >= TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(30)) && time < TimeSpan.FromHours(11).Add(TimeSpan.FromMinutes(30)))
            return true;
        if (time >= TimeSpan.FromHours(13).Add(TimeSpan.FromMinutes(30)) && time < TimeSpan.FromHours(15))
            return true;

        return false;
    }
}
