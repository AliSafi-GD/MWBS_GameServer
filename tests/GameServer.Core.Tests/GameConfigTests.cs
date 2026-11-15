using System.Text.Json;
using GameServer.Core;
using GameServer.Core.GameConfig;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServer.Core.Tests;

/// <summary>Game config files: format, loading, validation, mode decisions and the apply step. No database.</summary>
public sealed class GameConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gameconfig-tests-" + Guid.NewGuid().ToString("N"));

    public GameConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Folder(string name, params (string File, string Text)[] files)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, text) in files)
            File.WriteAllText(Path.Combine(folder, file), text);
        return folder;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static GameConfigFile File_(string key, string value, bool isPublic = false) =>
        new(key, Json(value), isPublic, key + ".json");

    private static ConfigEntry Entry(string key, string value, bool isPublic = false) =>
        new(key, Json(value), isPublic, DateTime.UtcNow);

    // ---- format ----

    [Fact]
    public void Format_reads_value_and_public_and_ignores_schema_and_comments()
    {
        var file = GameConfigFormat.Parse("economy", "economy.json", """
            {
              // a comment
              "$schema": "https://example.com/economy.schema.json",
              "_note": "ignored",
              "public": true,
              "value": { "currencies": { "coins": { "max": 100 } }, },
            }
            """);

        Assert.Equal("economy", file.Key);
        Assert.True(file.IsPublic);
        Assert.Equal(100, file.Value.GetProperty("currencies").GetProperty("coins").GetProperty("max").GetInt32());
    }

    [Fact]
    public void Format_public_defaults_to_false()
    {
        Assert.False(GameConfigFormat.Parse("social", "social.json", """{ "value": { "maxFriends": 5 } }""").IsPublic);
    }

    [Theory]
    [InlineData("""{ "value": 1, "pubic": true }""", "unknown field \"pubic\"")]
    [InlineData("""{ "public": true }""", "\"value\" is missing")]
    [InlineData("""{ "public": "yes", "value": 1 }""", "\"public\" must be true or false")]
    [InlineData("""[ 1 ]""", "must be a JSON object")]
    public void Format_rejects_bad_files_and_names_the_path(string text, string reason)
    {
        var ex = Assert.Throws<GameConfigException>(() => GameConfigFormat.Parse("social", "C:/mods/config/social.json", text));
        Assert.Contains("C:/mods/config/social.json", ex.Message);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Format_bad_json_names_the_path()
    {
        var ex = Assert.Throws<GameConfigException>(() => GameConfigFormat.Parse("shop", "mods/config/shop.json", "{ not json"));
        Assert.Contains("mods/config/shop.json", ex.Message);
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Serialize_output_is_read_back_by_the_parser()
    {
        var entry = Entry("social", """{ "maxFriends": 5, "groupTypes": { "clan": { "maxMembers": 30 } }, "text": "caf\u00e9 <b>" }""", isPublic: true);
        var file = GameConfigFormat.Parse("social", "social.json", GameConfigFormat.Serialize(entry));

        Assert.True(file.IsPublic);
        Assert.True(GameConfigPlanner.JsonEquals(entry.Value, file.Value));
    }

    // ---- loading ----

    [Fact]
    public void Loader_reads_valid_files_sorted_and_skips_underscore_and_other_files()
    {
        var folder = Folder("ok",
            ("social.json", """{ "value": { "maxFriends": 10 } }"""),
            ("app_version.json", """{ "public": true, "value": { "default": { "latest": "1.0.0" } } }"""),
            ("_readme.json", "this is not even json"),
            ("notes.txt", "ignored"));

        var files = GameConfigLoader.Load([new GameConfigSource("test", folder)]);

        Assert.Equal(["app_version", "social"], files.Select(f => f.Key));
        Assert.True(files[0].IsPublic);
    }

    [Fact]
    public void Loader_rejects_an_invalid_key_name_with_the_path()
    {
        var folder = Folder("badname", ("Shop.json", """{ "value": { "products": {} } }"""));

        var ex = Assert.Throws<GameConfigException>(() => GameConfigLoader.Load([new GameConfigSource("test", folder)]));
        Assert.Contains(Path.Combine(folder, "Shop.json"), ex.Message);
        Assert.Contains("Key must be", ex.Message);
    }

    [Fact]
    public void Loader_rejects_a_key_defined_in_two_sources_and_names_both_files()
    {
        var a = Folder("a", ("social.json", """{ "value": { "maxFriends": 10 } }"""));
        var b = Folder("b", ("social.json", """{ "value": { "maxFriends": 20 } }"""));

        var ex = Assert.Throws<GameConfigException>(() => GameConfigLoader.Load([new GameConfigSource("one", a), new GameConfigSource("two", b)]));
        Assert.Contains(Path.Combine(a, "social.json"), ex.Message);
        Assert.Contains(Path.Combine(b, "social.json"), ex.Message);
        Assert.Contains("defined twice", ex.Message);
    }

    [Fact]
    public void Loader_reports_bad_json_with_the_file_path_and_collects_every_problem()
    {
        var folder = Folder("broken",
            ("shop.json", "{ oops"),
            ("quests.json", """{ "value": { "quests": { "q": { "name": "no stat", "target": 1 } } } }"""),
            ("social.json", """{ "value": { "maxFriends": 10 } }"""));

        var ex = Assert.Throws<GameConfigException>(() => GameConfigLoader.Load([new GameConfigSource("test", folder)]));
        Assert.Contains(Path.Combine(folder, "shop.json"), ex.Message);
        Assert.Contains(Path.Combine(folder, "quests.json"), ex.Message); // semantic error found by the quest parser
        Assert.DoesNotContain("social.json", ex.Message);
    }

    [Fact]
    public void Loader_reports_a_missing_folder()
    {
        var ex = Assert.Throws<GameConfigException>(() => GameConfigLoader.Load([new GameConfigSource("extra", Path.Combine(_dir, "nope"))]));
        Assert.Contains("does not exist", ex.Message);
    }

    // ---- the real sample ----

    private static string SampleConfigFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "HelloGame.Modules", "config");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("samples/HelloGame.Modules/config not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_sample_module_config_validates()
    {
        var files = GameConfigLoader.Load([new GameConfigSource("sample", SampleConfigFolder())]);

        Assert.Equal(
            ["achievements", "daily_rewards", "leaderboards", "matches", "quests", "shop", "tournaments"],
            files.Select(f => f.Key));
    }

    [Fact]
    public void Every_sample_file_survives_an_export_and_load_round_trip()
    {
        foreach (var file in GameConfigLoader.Load([new GameConfigSource("sample", SampleConfigFolder())]))
        {
            var text = GameConfigFormat.Serialize(new ConfigEntry(file.Key, file.Value, file.IsPublic, DateTime.UtcNow));
            var back = GameConfigFormat.Parse(file.Key, file.Path, text);
            Assert.True(GameConfigPlanner.JsonEquals(file.Value, back.Value), file.Key);
            Assert.Equal(file.IsPublic, back.IsPublic);
        }
    }

    // ---- mode ----

    [Theory]
    [InlineData(null, GameConfigMode.Seed)]
    [InlineData("", GameConfigMode.Seed)]
    [InlineData("seed", GameConfigMode.Seed)]
    [InlineData("SYNC", GameConfigMode.Sync)]
    [InlineData(" off ", GameConfigMode.Off)]
    public void Mode_parses(string? text, GameConfigMode expected) => Assert.Equal(expected, GameConfigModes.Parse(text));

    [Fact]
    public void Mode_unknown_value_is_an_error() =>
        Assert.Contains("seed", Assert.Throws<GameConfigException>(() => GameConfigModes.Parse("always")).Message);

    // ---- planning ----

    [Fact]
    public void Seed_writes_only_missing_keys()
    {
        var files = new[] { File_("social", """{ "maxFriends": 10 }"""), File_("economy", """{ "currencies": {} }""") };
        var db = new[] { Entry("social", """{ "maxFriends": 99 }""") };

        var plan = GameConfigPlanner.Plan(files, db, GameConfigMode.Seed);

        Assert.Equal(GameConfigAction.Skip, plan.Single(s => s.File.Key == "social").Action);
        Assert.Equal(GameConfigAction.Write, plan.Single(s => s.File.Key == "economy").Action);
    }

    [Fact]
    public void Sync_writes_changed_values_and_leaves_equal_ones_even_when_property_order_differs()
    {
        var files = new[]
        {
            File_("social", """{ "maxFriends": 10, "maxPendingInvites": 5 }"""),
            File_("economy", """{ "currencies": { "coins": { "max": 100 } } }"""),
        };
        var db = new[]
        {
            Entry("social", """{ "maxPendingInvites": 5, "maxFriends": 10.0 }"""),  // same, reordered by jsonb
            Entry("economy", """{ "currencies": { "coins": { "max": 200 } } }"""),
        };

        var plan = GameConfigPlanner.Plan(files, db, GameConfigMode.Sync);

        Assert.Equal(GameConfigAction.Skip, plan.Single(s => s.File.Key == "social").Action);
        var economy = plan.Single(s => s.File.Key == "economy");
        Assert.Equal(GameConfigAction.Write, economy.Action);
        Assert.NotNull(economy.Previous);
    }

    [Fact]
    public void Sync_writes_when_only_the_public_flag_differs()
    {
        var plan = GameConfigPlanner.Plan([File_("social", "{}", isPublic: true)], [Entry("social", "{}", isPublic: false)], GameConfigMode.Sync);
        Assert.Equal(GameConfigAction.Write, plan.Single().Action);
        Assert.Contains("public", plan.Single().Reason);
    }

    [Fact]
    public void Maintenance_is_always_public_so_a_private_file_matches_a_public_row()
    {
        var file = File_("maintenance", """{ "enabled": false }""", isPublic: false);
        Assert.True(file.EffectivePublic);
        var plan = GameConfigPlanner.Plan([file], [Entry("maintenance", """{ "enabled": false }""", isPublic: true)], GameConfigMode.Sync);
        Assert.Equal(GameConfigAction.Skip, plan.Single().Action);
    }

    [Fact]
    public void Off_plans_nothing_and_no_mode_plans_a_delete()
    {
        var files = new[] { File_("social", "{}") };
        var db = new[] { Entry("social", """{ "x": 1 }"""), Entry("only_in_db", "{}") };

        Assert.Empty(GameConfigPlanner.Plan(files, db, GameConfigMode.Off));
        foreach (var mode in new[] { GameConfigMode.Seed, GameConfigMode.Sync })
            Assert.DoesNotContain(GameConfigPlanner.Plan(files, db, mode), s => s.File.Key == "only_in_db");
    }

    // ---- applying (fake store) ----

    private sealed class FakeStore : IConfigStore
    {
        public Dictionary<string, ConfigEntry> Rows { get; } = new();
        public List<string> Writes { get; } = new();
        public List<string> Deletes { get; } = new();

        public Task<IReadOnlyList<ConfigEntry>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConfigEntry>>(Rows.Values.ToList());

        public Task<ConfigEntry?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Rows.GetValueOrDefault(key));

        public Task<ConfigEntry> SetAsync(string key, JsonElement value, bool isPublic, CancellationToken ct = default)
        {
            Writes.Add(key);
            return Task.FromResult(Rows[key] = new ConfigEntry(key, value, isPublic, DateTime.UtcNow));
        }

        public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
        {
            Deletes.Add(key);
            return Task.FromResult(Rows.Remove(key));
        }
    }

    private sealed class FakeRecorder : IGameConfigRecorder
    {
        public List<(string Key, bool HadPrevious)> Calls { get; } = new();

        public Task BeforeWriteAsync(GameConfigStep step, CancellationToken ct)
        {
            Calls.Add((step.File.Key, step.Previous is not null));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Apply_seed_adds_missing_keys_and_keeps_edited_ones()
    {
        var store = new FakeStore();
        store.Rows["social"] = Entry("social", """{ "maxFriends": 99 }""");
        var recorder = new FakeRecorder();
        var files = new[] { File_("social", """{ "maxFriends": 10 }"""), File_("economy", """{ "currencies": {} }""", isPublic: true) };

        await GameConfigApplier.ApplyAsync(files, GameConfigMode.Seed, store, recorder, NullLogger.Instance);

        Assert.Equal(["economy"], store.Writes);
        Assert.Equal(99, store.Rows["social"].Value.GetProperty("maxFriends").GetInt32());
        Assert.True(store.Rows["economy"].IsPublic);
        Assert.Equal([("economy", false)], recorder.Calls);
    }

    [Fact]
    public async Task Apply_sync_overwrites_differences_records_the_old_value_and_deletes_nothing()
    {
        var store = new FakeStore();
        store.Rows["social"] = Entry("social", """{ "maxFriends": 99 }""");
        store.Rows["only_in_db"] = Entry("only_in_db", """{ "x": 1 }""");
        var recorder = new FakeRecorder();

        await GameConfigApplier.ApplyAsync([File_("social", """{ "maxFriends": 10 }""")], GameConfigMode.Sync, store, recorder, NullLogger.Instance);

        Assert.Equal(10, store.Rows["social"].Value.GetProperty("maxFriends").GetInt32());
        Assert.Equal([("social", true)], recorder.Calls);
        Assert.Empty(store.Deletes);
        Assert.True(store.Rows.ContainsKey("only_in_db"));

        // a second start with the same files writes nothing
        store.Writes.Clear();
        await GameConfigApplier.ApplyAsync([File_("social", """{ "maxFriends": 10 }""")], GameConfigMode.Sync, store, recorder, NullLogger.Instance);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Apply_off_touches_nothing()
    {
        var store = new FakeStore();
        await GameConfigApplier.ApplyAsync([File_("social", "{}")], GameConfigMode.Off, store, null, NullLogger.Instance);
        Assert.Empty(store.Writes);
    }

    // ---- paths ----

    [Fact]
    public void Relative_paths_resolve_against_the_content_root_and_absolute_ones_stay()
    {
        var root = Path.Combine(_dir, "app", "host");
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "app", "modules")), ContentPaths.Resolve("../modules", root));
        Assert.Equal(Path.Combine(root, "modules"), ContentPaths.Resolve("modules", root));
        Assert.Equal(_dir, ContentPaths.Resolve(_dir, root));
    }
}
