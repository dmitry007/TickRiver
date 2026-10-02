using System.Globalization;

namespace TickRiver.Core;

public sealed record Client(long Id, string Name, string Currency, decimal Rate, bool Archived)
{
    public string Status => Archived ? "Archived" : "Active";
}

public sealed record Project(long Id, long ClientId, string Name, string ClientName, string Currency,
    decimal ClientRate, decimal? RateOverride, bool Billable, bool Archived, bool ClientArchived)
{
    public decimal EffectiveRate => RateOverride ?? ClientRate;
    public string DisplayName => ClientName.Length == 0 ? Name : $"{Name} · {ClientName}";
    public string RateLabel => $"{Currency} {EffectiveRate:0.00}/h" + (RateOverride == null ? " (client rate)" : " (override)");
    public string Status => Archived ? "Archived" : ClientArchived ? "Client archived" : "Active";
}

public sealed record TimeEntry(long Id, long ProjectId, long ClientId, string ProjectName, string ClientName,
    string Description, DateTimeOffset Start, DateTimeOffset? End, decimal Rate, string Currency,
    bool Billable, bool Invoiced)
{
    public long Seconds => End.HasValue ? Math.Max(0, (End.Value.UtcTicks - Start.UtcTicks) / TimeSpan.TicksPerSecond) : 0;
    public string Duration => TimeFormat.Duration(Seconds);
    public string ProjectLabel => $"{ProjectName} · {ClientName}";
    public string DateLabel => Start.LocalDateTime.ToString("ddd, MMM d, yyyy");
    public string StartLabel => Start.LocalDateTime.ToString("HH:mm:ss");
    public string EndLabel => End?.LocalDateTime.ToString("HH:mm:ss") ?? "Running";
    public string BillingLabel => !Billable ? "Non-billable" : Invoiced ? "Invoiced" : "Unbilled";
}

public sealed record ReportRow(TimeEntry Entry, DateOnly Day, DateTimeOffset Start, DateTimeOffset End)
{
    public long Seconds => (End.UtcTicks - Start.UtcTicks) / TimeSpan.TicksPerSecond;
    public decimal DecimalHours => Seconds / 3600m;
    public decimal Amount => Entry.Billable ? DecimalHours * Entry.Rate : 0;
    public string Duration => TimeFormat.Duration(Seconds);
    public string DateLabel => Day.ToString("yyyy-MM-dd");
    public string ProjectLabel => Entry.ProjectLabel;
    public string Description => Entry.Description;
    public string StartLabel => Start.LocalDateTime.ToString("HH:mm:ss");
    public string EndLabel => End.LocalDateTime.ToString("HH:mm:ss");
    public string RateLabel => $"{Entry.Currency} {Entry.Rate:0.00}";
    public string AmountLabel => $"{Entry.Currency} {Amount:0.00}";
    public string BillingLabel => Entry.BillingLabel;
}

public sealed record DailyHours(DateOnly Day, long Seconds)
{
    public decimal Hours => Seconds / 3600m;
}

public sealed record Report(DateOnly From, DateOnly To, string Title, IReadOnlyList<ReportRow> Rows,
    IReadOnlyList<DailyHours> Days)
{
    public long Seconds => Rows.Sum(r => r.Seconds);
    public string Duration => TimeFormat.Duration(Seconds);
    public string RangeLabel => $"{From:MMM d, yyyy} – {To:MMM d, yyyy}";
    public string AmountLabel => string.Join(" · ", Rows.Where(r => r.Entry.Billable)
        .GroupBy(r => r.Entry.Currency).Select(g => $"{g.Key} {g.Sum(r => r.Amount):N2}"));
}

public static class TimeFormat
{
    public static string Duration(long seconds) => $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    public static DateTimeOffset Now => new(DateTimeOffset.UtcNow.UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    public static DateTimeOffset ParseLocal(DateTime date, string time, DateTimeOffset? original = null, TimeZoneInfo? zone = null)
    {
        if (!TimeOnly.TryParse(time, CultureInfo.CurrentCulture, DateTimeStyles.None, out var clock))
            throw new ArgumentException("Enter a time such as 09:30 or 09:30:15.");
        var value = DateTime.SpecifyKind(date.Date + clock.ToTimeSpan(), DateTimeKind.Unspecified);
        zone ??= TimeZoneInfo.Local;
        if (original.HasValue && TimeZoneInfo.ConvertTime(original.Value, zone).DateTime == value) return original.Value;
        if (zone.IsInvalidTime(value) || zone.IsAmbiguousTime(value))
            throw new ArgumentException("This time falls within a daylight-saving clock change. Choose an unambiguous time.");
        return new DateTimeOffset(value, zone.GetUtcOffset(value));
    }
}
