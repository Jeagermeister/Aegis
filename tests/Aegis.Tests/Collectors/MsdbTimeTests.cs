using Aegis.Collectors;

namespace Aegis.Tests.Collectors;

/// <summary>DESIGN-v2 asked for exactly these: packed datetimes, durations over 24 h, midnight, and the local-time trap.</summary>
public class MsdbTimeTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Fact]
    public void Packed_pair_in_a_utc_instance_maps_straight_through()
    {
        var result = MsdbTime.ToUtc(20260903, 140509, Utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 3, 14, 5, 9, TimeSpan.Zero), result);
    }

    [Fact]
    public void Packed_pair_is_local_wall_clock_time_and_is_shifted_to_utc()
    {
        // 14:05:09 Central Daylight Time is 19:05:09 UTC.
        var result = MsdbTime.ToUtc(20260903, 140509, Chicago);

        Assert.Equal(new DateTimeOffset(2026, 9, 3, 19, 5, 9, TimeSpan.Zero), result);
    }

    [Fact]
    public void Winter_dates_use_the_standard_offset()
    {
        // 14:00 Central Standard Time is 20:00 UTC.
        var result = MsdbTime.ToUtc(20260115, 140000, Chicago);

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Midnight_packs_as_zero_and_is_a_time_not_a_missing_value()
    {
        var result = MsdbTime.ToUtc(20260904, 0, Utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Never_ran_sentinel_is_null()
    {
        Assert.Null(MsdbTime.ToUtc(0, 0, Utc));
    }

    [Fact]
    public void A_time_inside_the_dst_gap_resolves_instead_of_throwing()
    {
        // 02:30 on 2026-03-08 does not exist in Chicago; clocks jump from 02:00 to 03:00.
        var result = MsdbTime.ToUtc(20260308, 23000, Chicago);

        Assert.NotNull(result);
    }

    [Fact]
    public void Datetime_columns_are_local_too()
    {
        var serverLocal = new DateTime(2026, 9, 3, 14, 5, 9, DateTimeKind.Unspecified);

        var result = MsdbTime.ToUtc(serverLocal, Chicago);

        Assert.Equal(new DateTimeOffset(2026, 9, 3, 19, 5, 9, TimeSpan.Zero), result);
        Assert.Null(MsdbTime.ToUtc((DateTime?)null, Chicago));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(45, 0, 0, 45)]
    [InlineData(13045, 1, 30, 45)]
    [InlineData(235959, 23, 59, 59)]
    [InlineData(250000, 25, 0, 0)]
    [InlineData(1234500, 123, 45, 0)]
    public void Durations_unpack_as_hours_minutes_seconds_with_unbounded_hours(int packed, int hours, int minutes, int seconds)
    {
        Assert.Equal(new TimeSpan(hours, minutes, seconds), MsdbTime.ParseDuration(packed));
    }

    [Fact]
    public void Execution_key_ignores_sub_second_precision()
    {
        var job = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var fromActivity = MsdbTime.ExecutionKey(job, new DateTimeOffset(2026, 9, 3, 14, 5, 9, 873, TimeSpan.Zero));
        var fromHistory = MsdbTime.ExecutionKey(job, new DateTimeOffset(2026, 9, 3, 14, 5, 9, TimeSpan.Zero));

        Assert.Equal(fromHistory, fromActivity);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e:20260903140509", fromActivity);
    }
}
