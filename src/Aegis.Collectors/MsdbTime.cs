using System.Globalization;

namespace Aegis.Collectors;

/// <summary>
/// The msdb datetime traps, in one place. SQL Agent stores run dates as two packed integers
/// (<c>run_date</c> = yyyyMMdd, <c>run_time</c> = HHmmss), durations as one (HHmmss, hours
/// unbounded), and all of it in the <em>instance's local time</em>, not UTC. AEGIS stores UTC.
/// </summary>
internal static class MsdbTime
{
    /// <summary>
    /// Converts a packed local date/time pair to UTC. Returns null for the "never ran" sentinel
    /// (<c>run_date</c> = 0). A local time that falls inside a DST gap resolves with the zone's
    /// standard offset rather than throwing; being an hour out beats losing the run.
    /// </summary>
    public static DateTimeOffset? ToUtc(int packedDate, int packedTime, TimeZoneInfo sourceZone)
    {
        if (packedDate <= 0)
        {
            return null;
        }

        var local = new DateTime(
            packedDate / 10000, packedDate / 100 % 100, packedDate % 100,
            packedTime / 10000, packedTime / 100 % 100, packedTime % 100,
            DateTimeKind.Unspecified);

        return ToUtc(local, sourceZone);
    }

    /// <summary>Converts an msdb DATETIME column (local wall-clock time) to UTC.</summary>
    public static DateTimeOffset? ToUtc(DateTime? serverLocal, TimeZoneInfo sourceZone)
        => serverLocal is { } local
            ? ToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), sourceZone)
            : null;

    private static DateTimeOffset ToUtc(DateTime unspecifiedLocal, TimeZoneInfo sourceZone)
    {
        var offset = sourceZone.GetUtcOffset(unspecifiedLocal);
        return new DateTimeOffset(unspecifiedLocal, offset).ToUniversalTime();
    }

    /// <summary>Unpacks <c>run_duration</c> (HHmmss). Hours are not capped at 24 or at 99: 1234500 is 123 h 45 min.</summary>
    public static TimeSpan ParseDuration(int packedDuration)
    {
        if (packedDuration <= 0)
        {
            return TimeSpan.Zero;
        }

        return new TimeSpan(packedDuration / 10000, packedDuration / 100 % 100, packedDuration % 100);
    }

    /// <summary>
    /// The execution key SQL Agent itself implies: it has no execution id, so an execution is
    /// (job, start second). Both <c>sysjobactivity</c> and the <c>sysjobhistory</c> outcome row
    /// yield the same key, which is what lets an in-flight run be closed by its outcome.
    /// </summary>
    public static string ExecutionKey(Guid jobId, DateTimeOffset startedAtUtc)
        => string.Create(CultureInfo.InvariantCulture, $"{jobId:D}:{startedAtUtc.UtcDateTime:yyyyMMddHHmmss}");
}
