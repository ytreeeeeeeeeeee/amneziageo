using System.Text.Json;
using AmneziaGeo.Android.Ui.Services;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The one-time move off named profiles, run against a hand-seeded pre-migration database.
/// </summary>
public sealed class ProfileMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ageo-mig-{Guid.NewGuid():N}.db");

    /// <inheritdoc />
    public void Dispose()
    {
        ClearPool();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + ".pre-profiles.bak" })
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task SelectedProfile_HandsItsConfigAndListToTheGlobalSelection()
    {
        Seed(
            "INSERT INTO settings (key, value, updated_at) VALUES ('selected-target', 'work', '2026-01-01');"
            + "INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)"
            + " VALUES ('home', 'nl', 7, 1, '2026-01-02'), ('work', 'de', 42, 1, '2026-01-01');");

        var store = await OpenAsync();

        Assert.Equal("de", await store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.Equal([42L], await store.GetSelectedRoutingListsAsync());
        Assert.Equal(42, await store.GetSelectedRoutingListAsync());
        Assert.False(TableExists("profiles"));
    }

    [Fact]
    public async Task NoSelection_TakesTheMostRecentlyTouchedProfile()
    {
        Seed(
            "INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)"
            + " VALUES ('old', 'nl', 7, 1, '2026-01-01'), ('fresh', 'de', 42, 1, '2026-02-01');");

        var store = await OpenAsync();

        Assert.Equal("de", await store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.Equal(42, await store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task RoutingOff_LeavesTheGlobalListEmpty()
    {
        Seed(
            "INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)"
            + " VALUES ('work', 'de', 42, 0, '2026-01-01');");

        var store = await OpenAsync();

        Assert.Equal("de", await store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.Null(await store.GetSelectedRoutingListAsync());
    }

    [Fact]
    public async Task EmptyProfileTable_LeavesAConfigSelectionAlone()
    {
        Seed("INSERT INTO settings (key, value, updated_at) VALUES ('selected-target', 'de', '2026-01-01');");

        var store = await OpenAsync();

        Assert.Equal("de", await store.GetSettingAsync(StateKeys.SelectedTarget));
        Assert.False(TableExists("profiles"));
    }

    [Fact]
    public async Task Migration_KeepsTheConfigsAndLeavesABackup()
    {
        Seed(
            "INSERT INTO configs (name, config_text, updated_at) VALUES ('de', 'conf', '2026-01-01');"
            + "INSERT INTO profiles (name, config, routing_list_id, use_routing, updated_at)"
            + " VALUES ('work', 'de', NULL, 0, '2026-01-01');");

        var store = await OpenAsync();

        Assert.True(await store.ConfigExistsAsync("de"));
        Assert.True(File.Exists(_dbPath + ".pre-profiles.bak"));
    }

    [Fact]
    public async Task LegacyScalarSurvivesRestartAndExplicitOffIsPersisted()
    {
        Seed("INSERT INTO settings (key, value, updated_at) VALUES ('selected-routing-list', '42', '2026-01-01');");
        var store = await OpenAsync();
        Assert.Equal([42L], await store.GetSelectedRoutingListsAsync());

        var reopened = await OpenAsync();
        Assert.Equal(42L, await reopened.GetSelectedRoutingListAsync());
        await reopened.SetSelectedRoutingListAsync(null);
        Assert.Empty((await (await OpenAsync()).GetSelectedRoutingListsAsync())!);
    }

    [Fact]
    public async Task MissingLegacyListIsClearedWithNoDanglingReference()
    {
        Seed("INSERT INTO settings (key, value, updated_at) VALUES ('selected-routing-list', '999', '2026-01-01');");
        var store = await OpenAsync();
        Assert.Empty((await store.GetSelectedRoutingListsAsync())!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateSelectedRoutingListsAsync(_ => [42, 999]));
        Assert.Empty((await store.GetSelectedRoutingListsAsync())!);
    }

    [Fact]
    public async Task TwoStoreInstancesUpdateTheSelectionWithoutLosingAnId()
    {
        Seed(string.Empty);
        var first = await OpenAsync();
        var second = await OpenAsync();
        Assert.Null(await first.GetSelectedRoutingListsAsync());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var addFirst = Task.Run(async () =>
        {
            await start.Task;
            await first.UpdateSelectedRoutingListsAsync(current => [.. current ?? [], 42, 42]);
        });
        var addSecond = Task.Run(async () =>
        {
            await start.Task;
            await second.UpdateSelectedRoutingListsAsync(current => [.. current ?? [], 7]);
        });
        start.SetResult();
        await Task.WhenAll(addFirst, addSecond);
        Assert.Equal([7L, 42L], await first.GetSelectedRoutingListsAsync());

        await second.RemoveRoutingListAsync(7);
        Assert.Equal([42L], await first.GetSelectedRoutingListsAsync());
        await second.RemoveRoutingListAsync(42);
        Assert.Empty((await first.GetSelectedRoutingListsAsync())!);
    }

    [Fact]
    public void AndroidRoutingMigrationDoesNotSaveAgentSettings()
    {
        // The Android agent itself cannot run in the portable migration test suite.
        using var source = new StreamReader(Assert.IsAssignableFrom<Stream>(
            typeof(ProfileMigrationTests).Assembly.GetManifestResourceStream("AndroidAgentConnection.cs")));
        var code = source.ReadToEnd();
        var start = code.IndexOf("private async Task InitAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "The Android initializer must be present.");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "The Android initializer must have a closing brace.");
        var initializer = code[start..end];

        Assert.Contains("TryMigrateAndroidRoutingListAsync(", initializer, StringComparison.Ordinal);
        Assert.DoesNotContain("Save(", initializer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("7", true)]
    [InlineData("[]", false)]
    [InlineData("[7]", false)]
    public async Task AndroidJsonChoiceMigratesOnceWithoutReplacingANewChoice(string? stored, bool expected)
    {
        Seed(string.Empty);
        var before = await OpenAsync();
        if (stored is not null)
        {
            await before.SetSettingAsync(StateKeys.SelectedRoutingList, stored);
        }

        using var legacy = JsonDocument.Parse("""{"SelectedRouting":42}""");
        var id = Assert.IsType<LegacyRoutingSelection>(LegacyRoutingSelection.Read(legacy.RootElement)).ListId;
        var migrated = await OpenAsync();
        Assert.Equal(expected, await migrated.TryMigrateAndroidRoutingListAsync(id));
        Assert.False(await migrated.TryMigrateAndroidRoutingListAsync(id));
        Assert.Equal(expected ? [42L] : stored == "[]" ? [] : new[] { 7L }, await migrated.GetSelectedRoutingListsAsync());

        // A restart can read the same legacy JSON after the user has changed the SQLite choice.
        await migrated.SetSelectedRoutingListAsync(7);
        var restarted = await OpenAsync();
        Assert.False(await restarted.TryMigrateAndroidRoutingListAsync(id));
        Assert.Equal([7L], await restarted.GetSelectedRoutingListsAsync());
    }

    [Theory]
    [InlineData("42|1", true, 42L)]
    [InlineData("42|0", true, null)]
    [InlineData("42", false, null)]
    public void AndroidLegacyProfileDistinguishesAnExplicitOffChoice(string token, bool hasChoice, long? expectedId)
    {
        using var legacy = JsonDocument.Parse(JsonSerializer.Serialize(new { Routing = new { work = token } }));
        var choice = LegacyRoutingSelection.ReadProfile(legacy.RootElement, "work");
        Assert.Equal(hasChoice, choice is not null);
        Assert.Equal(expectedId, choice?.ListId);
    }

    [Fact]
    public void AndroidLegacyOffChoiceSurvivesASettingsSaveBeforeMigration()
    {
        using var saved = JsonDocument.Parse("""{"SelectedRouting":0}""");
        var choice = Assert.IsType<LegacyRoutingSelection>(LegacyRoutingSelection.Read(saved.RootElement));
        Assert.Null(choice.ListId);
    }

    // A database as the previous build left it: schema version 1, a profiles table, and the tables the
    // migration reads.
    private void Seed(string rows)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 1;

            CREATE TABLE IF NOT EXISTS settings (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                key        TEXT NOT NULL UNIQUE,
                value      TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS configs (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL UNIQUE,
                config_text TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS routing_lists (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL,
                routes_json TEXT NOT NULL,
                domains_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            INSERT INTO routing_lists (id, name, routes_json, domains_json, updated_at)
            VALUES (7, 'Home', '[]', '[]', '2026-01-01'), (42, 'Work', '[]', '[]', '2026-01-01');

            CREATE TABLE IF NOT EXISTS profiles (
                name            TEXT PRIMARY KEY,
                config          TEXT NOT NULL,
                routing_list_id INTEGER,
                use_routing     INTEGER NOT NULL DEFAULT 0,
                updated_at      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS profile_state (
                name       TEXT PRIMARY KEY,
                status     TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """ + rows;
        command.ExecuteNonQuery();
    }

    private async Task<SqliteStateStore> OpenAsync()
    {
        var store = new SqliteStateStore(_dbPath);
        await store.InitializeAsync();
        return store;
    }

    // Drops the pooled connections to this test's database alone.
    private void ClearPool()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private bool TableExists(string name)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
