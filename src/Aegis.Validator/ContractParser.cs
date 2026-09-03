using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Converters;
using YamlDotNet.Serialization.NamingConventions;

namespace Aegis.Validator;

/// <summary>
/// Reads a contract from YAML and produces the canonical JSON that is hashed and stored. The
/// YAML is the human-editable form (TECH-STACK: contracts are YAML, diffable in git); the JSON is
/// the canonical form whose SHA-256 is <c>ContractVersion.SpecHash</c>. A contract that parses but
/// fails validation throws <see cref="ContractSpecException"/> rather than being stored.
/// </summary>
public static class ContractParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new TimeOnlyConverter(System.Globalization.CultureInfo.InvariantCulture, doubleQuotes: false, formats: ["HH:mm", "HH:mm:ss"]))
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Parses and validates a contract from YAML text.</summary>
    public static ContractSpec Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        ContractSpec spec;
        try
        {
            spec = Deserializer.Deserialize<ContractSpec>(yaml) ?? throw new ContractSpecException("contract is empty");
        }
        catch (ContractSpecException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ContractSpecException($"contract is not valid YAML: {ex.Message}");
        }

        spec.Validate();
        return spec;
    }

    /// <summary>The canonical JSON form of a validated contract. Deterministic: property order is fixed by the model.</summary>
    public static string ToCanonicalJson(ContractSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return JsonSerializer.Serialize(spec, JsonOptions);
    }

    /// <summary>SHA-256 hex (64 chars) of the canonical JSON, matching <c>ContractVersion.SpecHash CHAR(64)</c>.</summary>
    public static string ComputeSpecHash(ContractSpec spec)
    {
        var json = ToCanonicalJson(spec);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
