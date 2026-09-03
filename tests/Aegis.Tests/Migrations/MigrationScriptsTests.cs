using System.Globalization;
using Aegis.Migrations;

namespace Aegis.Tests.Migrations;

public class MigrationScriptsTests
{
    [Fact]
    public void Scripts_are_embedded_and_numbered_without_gaps()
    {
        var scripts = MigrationScripts.EmbeddedScripts.ToList();

        Assert.NotEmpty(scripts);

        // Resource names look like "Aegis.Migrations.Scripts.0003_CollectorState.sql".
        var numbers = scripts
            .Select(name => name.Split('.')[^2])
            .Select(fileName => int.Parse(fileName[..4], CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers);
    }

    [Fact]
    public void The_collector_state_migration_is_present()
    {
        Assert.Contains(MigrationScripts.EmbeddedScripts, name => name.EndsWith("0003_CollectorState.sql", StringComparison.Ordinal));
    }
}
