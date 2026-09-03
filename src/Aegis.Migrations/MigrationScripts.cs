using System.Reflection;

namespace Aegis.Migrations;

public static class MigrationScripts
{
    public static IEnumerable<string> EmbeddedScripts =>
        Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql"))
            .OrderBy(n => n);
}
