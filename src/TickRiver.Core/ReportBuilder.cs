namespace TickRiver.Core;

public static class ReportBuilder
{
    // Half-open local calendar boundaries include the entire final day, and handle DST.
    public static Report Build(IEnumerable<TimeEntry> entries, DateOnly from, DateOnly to, string title,
        long? clientId = null, long? projectId = null, bool unbilledOnly = false, TimeZoneInfo? zone = null)
    {
        if (to < from || to.DayNumber - from.DayNumber > 3660)
            throw new ArgumentException("Choose a range in order, up to ten years long.");
        zone ??= TimeZoneInfo.Local;
        var rows = new List<ReportRow>();
        var days = new List<DailyHours>();
        var selected = entries.Where(e => e.End.HasValue && (!clientId.HasValue || e.ClientId == clientId)
            && (!projectId.HasValue || e.ProjectId == projectId)
            && (!unbilledOnly || e.Billable && !e.Invoiced)).ToArray();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var start = DayBoundary(day, zone);
            var end = DayBoundary(day.AddDays(1), zone);
            long total = 0;
            foreach (var entry in selected)
            {
                var a = entry.Start.UtcDateTime > start ? entry.Start : new DateTimeOffset(start);
                var b = entry.End!.Value.UtcDateTime < end ? entry.End.Value : new DateTimeOffset(end);
                if (b <= a) continue;
                var row = new ReportRow(entry, day, TimeZoneInfo.ConvertTime(a, zone), TimeZoneInfo.ConvertTime(b, zone));
                rows.Add(row);
                total += row.Seconds;
            }
            days.Add(new DailyHours(day, total));
        }
        return new Report(from, to, title, rows.OrderBy(r => r.Start).ToArray(), days);
    }

    private static DateTime DayBoundary(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        // Some time zones advance at midnight, or even skip a whole calendar day.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).UtcDateTime;
    }
}
