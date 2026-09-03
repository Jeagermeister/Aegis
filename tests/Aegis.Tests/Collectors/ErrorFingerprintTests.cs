using Aegis.Collectors;

namespace Aegis.Tests.Collectors;

public class ErrorFingerprintTests
{
    private const string MondayFailure =
        "Executed as user: svc_etl. Could not find file 'C:\\landing\\BCBS_ELIG_20260901.csv' at 2026-09-01 03:15:22. [SQLSTATE 42000] (Error 4860).  The step failed.";

    private const string TuesdayFailure =
        "Executed as user: svc_etl. Could not find file 'C:\\landing\\BCBS_ELIG_20260902.csv' at 2026-09-02 03:14:58. [SQLSTATE 42000] (Error 4860).  The step failed.";

    [Fact]
    public void The_same_failure_on_different_days_shares_one_fingerprint()
    {
        Assert.Equal(ErrorFingerprint.Compute(MondayFailure), ErrorFingerprint.Compute(TuesdayFailure));
    }

    [Fact]
    public void Different_failures_do_not()
    {
        var loginFailed = "Executed as user: svc_etl. Login failed for user 'carrier_ro'. [SQLSTATE 28000] (Error 18456).  The step failed.";

        Assert.NotEqual(ErrorFingerprint.Compute(MondayFailure), ErrorFingerprint.Compute(loginFailed));
    }

    [Fact]
    public void Normalisation_strips_dates_times_numbers_hex_and_quoted_values()
    {
        var shape = ErrorFingerprint.Normalise("Timeout after 30s at 2026-09-01 03:15:22 reading 'feed.csv' from Table1 handle 0x1F (code 7)");

        Assert.Equal("TIMEOUT AFTER AT READING FROM TABLE1 HANDLE (CODE )", shape);
    }

    [Fact]
    public void Whitespace_and_case_differences_do_not_split_a_group()
    {
        var a = ErrorFingerprint.Compute("Could not   find\nstored procedure");
        var b = ErrorFingerprint.Compute("could not find stored procedure");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_fits_the_char32_column()
    {
        var fingerprint = ErrorFingerprint.Compute(MondayFailure);

        Assert.Equal(ErrorFingerprint.FingerprintLength, fingerprint.Length);
        Assert.Matches("^[0-9A-F]{32}$", fingerprint);
    }

    [Fact]
    public void Dedup_key_is_64_hex_characters_and_order_sensitive()
    {
        var key = ErrorFingerprint.DedupKey("CollectorZeroRows", "7");

        Assert.Matches("^[0-9A-F]{64}$", key);
        Assert.NotEqual(key, ErrorFingerprint.DedupKey("7", "CollectorZeroRows"));
        Assert.Equal(key, ErrorFingerprint.DedupKey("CollectorZeroRows", "7"));
    }
}
