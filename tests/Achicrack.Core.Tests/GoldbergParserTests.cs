using Achicrack.Core.Adapters.Goldberg;

namespace Achicrack.Core.Tests;

public class GoldbergStateParserTests
{
    [Fact]
    public void Parses_earned_entries_with_timestamps()
    {
        var snapshot = GoldbergStateParser.Parse("""
            { "ACH_A": { "earned": true, "earned_time": 1617203350 },
              "ACH_B": { "earned": false, "earned_time": 0 } }
            """);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.True(snapshot.Entries["ACH_A"].Earned);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1617203350), snapshot.Entries["ACH_A"].EarnedTimeUtc);
        Assert.False(snapshot.Entries["ACH_B"].Earned);
        Assert.Null(snapshot.Entries["ACH_B"].EarnedTimeUtc);
        Assert.Single(snapshot.Earned);
    }

    [Fact]
    public void Tolerates_fork_extensions_and_loose_types()
    {
        var snapshot = GoldbergStateParser.Parse("""
            { "ACH_INT": { "earned": 1, "earned_time": "1700000000", "progress": 5, "max_progress": 10 },
              "ACH_STR": { "earned": "true" },
              "ACH_NOTIME": { "earned": true } }
            """);

        Assert.True(snapshot.Entries["ACH_INT"].Earned);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), snapshot.Entries["ACH_INT"].EarnedTimeUtc);
        Assert.True(snapshot.Entries["ACH_STR"].Earned);
        Assert.True(snapshot.Entries["ACH_NOTIME"].Earned);
        Assert.Null(snapshot.Entries["ACH_NOTIME"].EarnedTimeUtc);
    }

    [Fact]
    public void Skips_malformed_entries_with_warnings()
    {
        var snapshot = GoldbergStateParser.Parse("""{ "ACH_OK": { "earned": true }, "ACH_BAD": 42, "ACH_WEIRD": { "earned": [] } }""");

        Assert.True(snapshot.Entries["ACH_OK"].Earned);
        Assert.False(snapshot.Entries.ContainsKey("ACH_BAD"));
        Assert.False(snapshot.Entries["ACH_WEIRD"].Earned);
        Assert.Equal(2, snapshot.Warnings.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"ACH_A\": { \"earned\": tr")]
    [InlineData("[1,2,3]")]
    [InlineData("not json")]
    public void Rejects_empty_truncated_or_wrong_root(string content)
    {
        Assert.Throws<FormatException>(() => GoldbergStateParser.Parse(content));
    }

    [Fact]
    public void Empty_object_is_valid_and_empty()
    {
        Assert.Empty(GoldbergStateParser.Parse("{}").Entries);
    }
}

public class GoldbergCatalogParserTests
{
    [Fact]
    public void Parses_localized_names_hidden_flags_and_icons()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("images"));
        File.WriteAllBytes(dir.Combine("images", "a.png"), [1, 2, 3]);

        var defs = GoldbergCatalogParser.Parse("""
            [
              { "name": "ACH_A", "displayName": "Alpha", "description": "First", "hidden": "0", "icon": "images/a.png", "icon_gray": "images/missing.png" },
              { "name": "ACH_B", "displayName": { "german": "Beta DE", "english": "Beta" }, "description": { "english": "Second" }, "hidden": 1 },
              { "name": "ACH_C", "displayName": { "french": "Gamma FR" }, "hidden": "1" },
              { "name": "ACH_D", "displayName": "", "hidden": true }
            ]
            """, dir.Path, "english");

        Assert.Equal(4, defs.Count);
        Assert.Equal("Alpha", defs[0].DisplayName);
        Assert.False(defs[0].Hidden);
        Assert.Equal(Path.GetFullPath(dir.Combine("images", "a.png")), defs[0].IconPath);
        Assert.Null(defs[0].IconGrayPath); // missing icon file → null (UI shows default trophy)
        Assert.Equal("Beta", defs[1].DisplayName);
        Assert.Equal("Second", defs[1].Description);
        Assert.True(defs[1].Hidden);
        Assert.Equal("Gamma FR", defs[2].DisplayName); // falls back to any available language
        Assert.Null(defs[3].DisplayName); // blank → unknown, not invented
        Assert.Equal([0, 1, 2, 3], defs.Select(d => d.SortOrder));
    }

    [Fact]
    public void Prefers_requested_language()
    {
        using var dir = new TempDir();
        var defs = GoldbergCatalogParser.Parse("""[ { "name": "A", "displayName": { "german": "DE", "english": "EN" } } ]""",
            dir.Path, "german");
        Assert.Equal("DE", defs[0].DisplayName);
    }

    [Fact]
    public void Skips_entries_without_name_and_duplicates()
    {
        using var dir = new TempDir();
        var warnings = new List<string>();
        var defs = GoldbergCatalogParser.Parse("""[ { "displayName": "x" }, { "name": "A" }, { "name": "A" }, 5 ]""",
            dir.Path, "english", warnings);
        Assert.Single(defs);
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public void Rejects_non_array()
    {
        using var dir = new TempDir();
        Assert.Throws<FormatException>(() => GoldbergCatalogParser.Parse("{}", dir.Path, "english"));
        Assert.Throws<FormatException>(() => GoldbergCatalogParser.Parse("[{", dir.Path, "english"));
    }
}
