namespace Relay.Core.Navigation;

public static class DateGrouping
{
    /// <summary>"Today", "Yesterday", weekday names for the last week, otherwise a full date.</summary>
    public static string Label(DateTime localTime, DateTime now)
    {
        var day = localTime.Date;
        var today = now.Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7)) return day.ToString("dddd");
        return day.Year == today.Year ? day.ToString("dddd, MMMM d") : day.ToString("MMMM d, yyyy");
    }
}
