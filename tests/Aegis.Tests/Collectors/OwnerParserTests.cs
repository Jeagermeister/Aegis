using Aegis.Collectors;

namespace Aegis.Tests.Collectors;

public class OwnerParserTests
{
    [Theory]
    [InlineData("Owner: ETL Team; Ticket: DE-123", "ETL Team", false)]
    [InlineData("owner = LPDE", "LPDE", false)]
    [InlineData("team=carrier-integration", "carrier-integration", false)]
    [InlineData("Team: Data Platform.", "Data Platform", false)]
    [InlineData("Nightly BCBS load. Ticket: DE-4821", "DE-4821", true)]
    [InlineData("Loads BCBS files #lpde nightly", "lpde", false)]
    [InlineData("Page @data-eng on failure", "data-eng", false)]
    [InlineData("Owner: ETL #nightly", "ETL", false)]
    [InlineData("Ticket: DE-1 Owner: ETL", "ETL", false)]
    public void Parses_the_forms_teams_already_use(string text, string owner, bool isTicket)
    {
        var match = OwnerParser.Parse(text);

        Assert.NotNull(match);
        Assert.Equal(owner, match.Value.Owner);
        Assert.Equal(isTicket, match.Value.IsTicket);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("No description available.")]
    [InlineData("See #4821 for details")]
    [InlineData("Steam: engine")]
    [InlineData("mail someone@example.com")]
    public void Returns_null_when_nothing_is_declared(string? text)
    {
        Assert.Null(OwnerParser.Parse(text));
    }

    [Fact]
    public void Value_stops_at_a_semicolon_or_line_break()
    {
        Assert.Equal("ETL", OwnerParser.Parse("Owner: ETL; runs nightly")!.Value.Owner);
        Assert.Equal("ETL", OwnerParser.Parse("Owner: ETL\nRuns nightly")!.Value.Owner);
    }
}
