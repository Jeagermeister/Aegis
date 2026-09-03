using Aegis.Validator;

namespace Aegis.Tests.Validator;

public sealed class ContractParserTests
{
    private const string ValidYaml = """
        feedId: BCBS-ELIG
        owner: ETL
        landingPrefix: landing/bcbs/eligibility/
        fileMask: BCBS_ELIG_{yyyyMMdd}.csv
        arrivalWindow:
          start: "02:00"
          end: "06:00"
        schema:
          name: Eligibility_v1
          columns:
            - name: MemberId
              type: string
              nullable: false
              maxLength: 50
            - name: TermDate
              type: date
              nullable: true
        """;

    [Fact]
    public void A_valid_contract_parses_into_the_expected_shape()
    {
        var spec = ContractParser.Parse(ValidYaml);

        Assert.Equal("BCBS-ELIG", spec.FeedId);
        Assert.Equal("ETL", spec.Owner);
        Assert.Equal("landing/bcbs/eligibility/", spec.LandingPrefix);
        Assert.Equal("BCBS_ELIG_{yyyyMMdd}.csv", spec.FileMask);
        Assert.Equal(new TimeOnly(2, 0), spec.ArrivalWindow.Start);
        Assert.Equal(new TimeOnly(6, 0), spec.ArrivalWindow.End);
        Assert.Equal("Eligibility_v1", spec.Schema.Name);
        Assert.Equal(2, spec.Schema.Columns.Count);
        Assert.Equal(("MemberId", "string", false, 50), (spec.Schema.Columns[0].Name, spec.Schema.Columns[0].Type, spec.Schema.Columns[0].Nullable, spec.Schema.Columns[0].MaxLength));
        Assert.Equal(("TermDate", "date", true), (spec.Schema.Columns[1].Name, spec.Schema.Columns[1].Type, spec.Schema.Columns[1].Nullable));
    }

    [Fact]
    public void The_spec_hash_is_stable_and_changes_when_the_spec_changes()
    {
        var spec = ContractParser.Parse(ValidYaml);
        var hash = ContractParser.ComputeSpecHash(spec);

        Assert.Equal(64, hash.Length);

        var changed = ContractParser.Parse(ValidYaml.Replace("owner: ETL", "owner: Claims", StringComparison.Ordinal));
        Assert.NotEqual(hash, ContractParser.ComputeSpecHash(changed));
    }

    [Fact]
    public void The_spec_hash_is_order_independent_for_equivalent_yaml()
    {
        var reordered = """
            fileMask: BCBS_ELIG_{yyyyMMdd}.csv
            owner: ETL
            landingPrefix: landing/bcbs/eligibility/
            feedId: BCBS-ELIG
            arrivalWindow:
              end: "06:00"
              start: "02:00"
            schema:
              name: Eligibility_v1
              columns:
                - name: MemberId
                  type: string
                  nullable: false
                  maxLength: 50
                - name: TermDate
                  type: date
                  nullable: true
            """;

        Assert.Equal(ContractParser.ComputeSpecHash(ContractParser.Parse(ValidYaml)), ContractParser.ComputeSpecHash(ContractParser.Parse(reordered)));
    }

    [Theory]
    [InlineData("feedId: X\n", "owner is required")]
    [InlineData("feedId: X\nowner: ETL\nlandingPrefix: p\nfileMask: m\narrivalWindow:\n  start: \"06:00\"\n  end: \"02:00\"\nschema:\n  name: s\n  columns:\n    - name: c\n      type: string\n", "arrival window start must be before end")]
    [InlineData("feedId: X\nowner: ETL\nlandingPrefix: p\nfileMask: m\narrivalWindow:\n  start: \"02:00\"\n  end: \"06:00\"\nschema:\n  name: s\n  columns: []\n", "schema must declare at least one column")]
    public void An_invalid_contract_throws(string yaml, string expectedMessage)
    {
        var ex = Assert.Throws<ContractSpecException>(() => ContractParser.Parse(yaml));
        Assert.Contains(expectedMessage, ex.Message);
    }
}
