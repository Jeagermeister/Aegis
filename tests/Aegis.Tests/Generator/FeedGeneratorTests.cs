using Aegis.Generator;

namespace Aegis.Tests.Generator;

public sealed class FeedGeneratorTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 9, 3);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aegis-generator-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_zero_violation_rate_produces_a_clean_day()
    {
        var manifest = await new SyntheticFeedGenerator(seed: 1).GenerateAsync(Context(violationRate: 0));

        Assert.Equal(8, manifest.Files.Count);
        Assert.All(manifest.Files, file => Assert.Null(file.Violation));
        Assert.All(manifest.Files, file => Assert.True(File.Exists(Path.Combine(_root, file.RelativePath!)), file.RelativePath));
        Assert.True(File.Exists(Path.Combine(_root, "MANIFEST.json")));
    }

    [Fact]
    public async Task A_full_violation_rate_breaks_every_file_and_the_manifest_says_how()
    {
        var manifest = await new SyntheticFeedGenerator(seed: 7).GenerateAsync(Context(violationRate: 1));

        Assert.All(manifest.Files, file =>
        {
            Assert.NotNull(file.Violation);
            Assert.False(string.IsNullOrWhiteSpace(file.Violation!.Detail));
        });
    }

    [Fact]
    public async Task The_same_seed_reproduces_the_same_output()
    {
        var first = await new SyntheticFeedGenerator(seed: 42).GenerateAsync(Context(violationRate: 0.5, subdirectory: "a"));
        var second = await new SyntheticFeedGenerator(seed: 42).GenerateAsync(Context(violationRate: 0.5, subdirectory: "b"));

        Assert.Equal(
            first.Files.Select(file => (file.FeedId, file.RelativePath, file.RowCount, file.Violation)),
            second.Files.Select(file => (file.FeedId, file.RelativePath, file.RowCount, file.Violation)));

        foreach (var (a, b) in first.Files.Zip(second.Files))
        {
            if (a.RelativePath is null)
            {
                continue;
            }

            Assert.Equal(
                await File.ReadAllTextAsync(Path.Combine(_root, "a", a.RelativePath)),
                await File.ReadAllTextAsync(Path.Combine(_root, "b", b.RelativePath!)));
        }
    }

    [Fact]
    public async Task Schema_drift_is_visible_in_the_file_header_not_just_the_manifest()
    {
        // Enough seeds that every drift kind shows up somewhere; the assertions are per kind.
        var seen = new HashSet<ViolationType>();
        for (var seed = 0; seed < 60 && seen.Count < 3; seed++)
        {
            var manifest = await new SyntheticFeedGenerator(seed).GenerateAsync(Context(violationRate: 1, subdirectory: seed.ToString()));

            foreach (var file in manifest.Files.Where(file => file.Format == "csv" && file.RelativePath is not null))
            {
                var header = (await File.ReadAllLinesAsync(Path.Combine(_root, seed.ToString(), file.RelativePath!)))[0].Split(',');
                var schema = manifest.Schemas.Single(schema => schema.SchemaId == manifest.Feeds.Single(feed => feed.FeedId == file.FeedId).SchemaName);
                var expected = schema.Columns.Select(column => column.Name).ToList();

                switch (file.Violation?.Type)
                {
                    case ViolationType.SchemaDriftRenamedColumn:
                        Assert.Equal(expected.Count, header.Length);
                        Assert.Single(expected.Except(header));
                        Assert.Single(header.Except(expected));
                        seen.Add(ViolationType.SchemaDriftRenamedColumn);
                        break;
                    case ViolationType.SchemaDriftAddedColumn:
                        Assert.Equal(expected.Count + 1, header.Length);
                        Assert.StartsWith("Unexpected_Column_", header[^1], StringComparison.Ordinal);
                        seen.Add(ViolationType.SchemaDriftAddedColumn);
                        break;
                    case ViolationType.SchemaDriftRemovedColumn:
                        Assert.Equal(expected.Count - 1, header.Length);
                        Assert.Single(expected.Except(header));
                        seen.Add(ViolationType.SchemaDriftRemovedColumn);
                        break;
                    case null:
                    case ViolationType.MaskDrift:
                    case ViolationType.LateArrival:
                    case ViolationType.NullInNotNullColumn:
                    case ViolationType.DataTypeMismatch:
                    case ViolationType.TruncatedData:
                    case ViolationType.DuplicateRows:
                        Assert.Equal(expected, header);
                        break;
                }
            }
        }

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task Late_missing_and_drifted_files_land_where_the_manifest_says()
    {
        var seen = new HashSet<ViolationType>();
        for (var seed = 100; seed < 160 && seen.Count < 3; seed++)
        {
            var manifest = await new SyntheticFeedGenerator(seed).GenerateAsync(Context(violationRate: 1, subdirectory: seed.ToString()));
            foreach (var file in manifest.Files)
            {
                var feed = manifest.Feeds.Single(candidate => candidate.FeedId == file.FeedId);
                switch (file.Violation?.Type)
                {
                    case ViolationType.MissingFile:
                        Assert.Null(file.RelativePath);
                        Assert.Null(file.ActualArrival);
                        seen.Add(ViolationType.MissingFile);
                        break;
                    case ViolationType.LateArrival:
                        Assert.EndsWith(SyntheticFeedGenerator.LateFileSuffix, file.RelativePath, StringComparison.Ordinal);
                        Assert.True(file.ActualArrival > new DateTimeOffset(Day.ToDateTime(feed.ArrivalWindowEnd, DateTimeKind.Utc)));
                        seen.Add(ViolationType.LateArrival);
                        break;
                    case ViolationType.MaskDrift:
                        Assert.NotEqual(file.ExpectedFileName, Path.GetFileName(file.RelativePath));
                        seen.Add(ViolationType.MaskDrift);
                        break;
                }

                if (file.RelativePath is not null)
                {
                    Assert.True(File.Exists(Path.Combine(_root, seed.ToString(), file.RelativePath)), file.RelativePath);
                }
            }
        }

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task Fixed_width_feeds_never_get_a_truncation_violation()
    {
        for (var seed = 200; seed < 230; seed++)
        {
            var manifest = await new SyntheticFeedGenerator(seed).GenerateAsync(Context(violationRate: 1, subdirectory: seed.ToString()));
            Assert.DoesNotContain(manifest.Files, file => file.Format == "fixed-width" && file.Violation?.Type == ViolationType.TruncatedData);
        }
    }

    private GenerationContext Context(double violationRate, string? subdirectory = null)
        => SyntheticFeedGenerator.CreateDefaultContext(Day, subdirectory is null ? _root : Path.Combine(_root, subdirectory), violationRate);
}
