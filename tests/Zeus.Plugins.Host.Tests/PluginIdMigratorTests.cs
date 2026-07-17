// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using LiteDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Data;
using Zeus.Plugins.Contracts.Registry;
using Zeus.Plugins.Host.Registry;

namespace Zeus.Plugins.Host.Tests;

public sealed class PluginIdMigratorTests : IAsyncLifetime
{
    private const string OldDigitalId = "com.kb2uka.digital";
    private const string NewDigitalId = "org.openhpsdr.digital";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly PluginSettingsStore _store;
    private readonly List<PluginManager> _managers = new();
    private readonly List<ServiceProvider> _providers = new();

    private const string PluginSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Zeus.Plugins.Contracts;

        namespace Fixture;

        public sealed class P : IZeusPlugin
        {
            public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;
            public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
        }
        """;

    public PluginIdMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zeus-plugin-id-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "settings.db");
        _store = new PluginSettingsStore(_dbPath);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
            await manager.StopAsync(default);
        foreach (var provider in _providers)
            provider.Dispose();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task StartAsync_MigratesOldInstallToNewId_AndCopiesSettings()
    {
        WritePluginDir(OldDigitalId, "OldDigital", "1.0.0");
        await _store.ForPlugin(OldDigitalId).SetAsync("mode", "ft8");
        await _store.ForPlugin(OldDigitalId).SetAsync("gain", 37);

        var (zip, sha) = BuildPluginZip(NewDigitalId, "NewDigital", "1.2.0");
        var http = new RecordingHandler { Body = zip };
        var registry = RegistryWith(NewDigitalId, "1.2.0", sha);
        var manager = CreateManager(registry, new PluginPackageDownloader(new HttpClient(http)));

        await manager.StartAsync(default);

        Assert.Null(manager.Find(OldDigitalId));
        Assert.NotNull(manager.Find(NewDigitalId));
        Assert.False(Directory.Exists(Path.Combine(_root, OldDigitalId)));
        Assert.True(Directory.Exists(Path.Combine(_root, NewDigitalId)));
        Assert.Equal("ft8", await _store.ForPlugin(NewDigitalId).GetAsync<string>("mode"));
        Assert.Equal(37, await _store.ForPlugin(NewDigitalId).GetAsync<int>("gain"));
        Assert.Equal(1, registry.FetchCount);
        Assert.Equal(1, http.RequestCount);
    }

    [Fact]
    public async Task StartAsync_LeavesOldPluginActivatable_WhenRegistryUnavailable()
    {
        WritePluginDir(OldDigitalId, "OldDigitalOffline", "1.0.0");
        var registry = new StubRegistry { Exception = new RegistryFetchException("offline") };
        var manager = CreateManager(registry, new EmptyDownloader());

        await manager.StartAsync(default);

        Assert.NotNull(manager.Find(OldDigitalId));
        Assert.Null(manager.Find(NewDigitalId));
        Assert.True(Directory.Exists(Path.Combine(_root, OldDigitalId)));
        Assert.False(File.Exists(Path.Combine(_root, OldDigitalId, PluginManager.PendingDeleteMarker)));
        Assert.Equal(1, registry.FetchCount);
    }

    [Fact]
    public async Task StartAsync_IsIdempotentAcrossBoots()
    {
        WritePluginDir(OldDigitalId, "OldDigitalTwice", "1.0.0");
        await _store.ForPlugin(OldDigitalId).SetAsync("mode", "wspr");

        var (zip, sha) = BuildPluginZip(NewDigitalId, "NewDigitalTwice", "1.3.0");
        var registry = RegistryWith(NewDigitalId, "1.3.0", sha);
        var first = CreateManager(registry, new PluginPackageDownloader(
            new HttpClient(new RecordingHandler { Body = zip })));

        await first.StartAsync(default);
        await first.StopAsync(default);

        var secondRegistry = RegistryWith(NewDigitalId, "1.3.0", sha);
        var second = CreateManager(secondRegistry, new EmptyDownloader());
        await second.StartAsync(default);

        Assert.Null(second.Find(OldDigitalId));
        Assert.NotNull(second.Find(NewDigitalId));
        Assert.Equal("wspr", await _store.ForPlugin(NewDigitalId).GetAsync<string>("mode"));
        Assert.Equal(0, secondRegistry.FetchCount);
    }

    [Fact]
    public async Task StartAsync_CleansOldLeftover_WhenNewInstallAlreadyExists()
    {
        WritePluginDir(OldDigitalId, "OldDigitalLeftover", "1.0.0");
        WritePluginDir(NewDigitalId, "NewDigitalLeftover", "2.0.0");
        await _store.ForPlugin(OldDigitalId).SetAsync("waterfall", true);

        var registry = RegistryWith(NewDigitalId, "2.0.0", "");
        var manager = CreateManager(registry, new EmptyDownloader());

        await manager.StartAsync(default);

        Assert.Null(manager.Find(OldDigitalId));
        Assert.NotNull(manager.Find(NewDigitalId));
        Assert.False(Directory.Exists(Path.Combine(_root, OldDigitalId)));
        Assert.True(await _store.ForPlugin(NewDigitalId).GetAsync<bool>("waterfall"));
        Assert.Equal(0, registry.FetchCount);
    }

    [Fact]
    public async Task StartAsync_RestoreSettingsUsesDeleteManyInsertPattern()
    {
        WritePluginDir(OldDigitalId, "OldDigitalDuplicates", "1.0.0");

        using (var lease = SharedLiteDatabase.Acquire(_dbPath))
        {
            var coll = lease.Database.GetCollection<BsonDocument>(
                PluginSettingsStore.CollectionName(OldDigitalId));
            coll.Insert(new BsonDocument
            {
                ["_id"] = 1,
                ["Key"] = "profile",
                ["JsonValue"] = "\"stale\"",
            });
            coll.Insert(new BsonDocument
            {
                ["_id"] = 2,
                ["Key"] = "profile",
                ["JsonValue"] = "\"fresh\"",
            });
        }

        var (zip, sha) = BuildPluginZip(NewDigitalId, "NewDigitalDuplicates", "1.4.0");
        var manager = CreateManager(
            RegistryWith(NewDigitalId, "1.4.0", sha),
            new PluginPackageDownloader(new HttpClient(new RecordingHandler { Body = zip })));

        await manager.StartAsync(default);

        Assert.Equal("fresh", await _store.ForPlugin(NewDigitalId).GetAsync<string>("profile"));
        using var verify = SharedLiteDatabase.Acquire(_dbPath);
        var newColl = verify.Database.GetCollection<BsonDocument>(
            PluginSettingsStore.CollectionName(NewDigitalId));
        Assert.Equal(1, newColl.Count());
    }

    [Fact]
    public async Task StartAsync_MarksOldDirPendingDelete_WhenDirectoryCannotBeRemoved()
    {
        WritePluginDir(OldDigitalId, "OldDigitalLocked", "1.0.0");
        var (zip, sha) = BuildPluginZip(NewDigitalId, "NewDigitalLocked", "1.5.0");
        var manager = CreateManager(
            RegistryWith(NewDigitalId, "1.5.0", sha),
            new PluginPackageDownloader(new HttpClient(new RecordingHandler { Body = zip })),
            dir => !Path.GetFileName(dir).Equals(OldDigitalId, StringComparison.Ordinal));

        await manager.StartAsync(default);

        var oldDir = Path.Combine(_root, OldDigitalId);
        Assert.True(Directory.Exists(oldDir));
        Assert.True(File.Exists(Path.Combine(oldDir, PluginManager.PendingDeleteMarker)));
        Assert.Null(manager.Find(OldDigitalId));
        Assert.NotNull(manager.Find(NewDigitalId));
    }

    [Fact]
    public async Task StartAsync_LeavesOldActive_WhenPartialNewDirCannotBeRemoved_AndMigratesNextBoot()
    {
        WritePluginDir(OldDigitalId, "OldDigitalPartial", "1.0.0");
        var oldDir = Path.Combine(_root, OldDigitalId);
        var newDir = Path.Combine(_root, NewDigitalId);
        Directory.CreateDirectory(newDir);
        await File.WriteAllTextAsync(Path.Combine(newDir, "partial.txt"), "partial");

        var firstRegistry = RegistryWith(NewDigitalId, "2.0.0", "unused");
        var first = CreateManager(
            firstRegistry,
            new EmptyDownloader(),
            tryDeleteDirectory: dir => !SamePath(dir, newDir));

        await first.StartAsync(default);

        Assert.NotNull(first.Find(OldDigitalId));
        Assert.Null(first.Find(NewDigitalId));
        Assert.True(Directory.Exists(oldDir));
        Assert.True(File.Exists(Path.Combine(newDir, PluginManager.PendingDeleteMarker)));
        Assert.Equal(0, firstRegistry.FetchCount);

        await first.StopAsync(default);

        var (zip, sha) = BuildPluginZip(NewDigitalId, "NewDigitalPartial", "2.0.0");
        var secondRegistry = RegistryWith(NewDigitalId, "2.0.0", sha);
        var second = CreateManager(
            secondRegistry,
            new PluginPackageDownloader(new HttpClient(new RecordingHandler { Body = zip })));

        await second.StartAsync(default);

        Assert.Null(second.Find(OldDigitalId));
        Assert.NotNull(second.Find(NewDigitalId));
        // On Windows the first boot's ALC keeps the old plugin dll file-locked
        // in-process even after StopAsync, so the migrator's delete legitimately
        // falls back to the .pending-delete marker (swept on a later boot).
        // Either outcome means the old install is retired.
        Assert.True(
            !Directory.Exists(oldDir)
            || File.Exists(Path.Combine(oldDir, PluginManager.PendingDeleteMarker)));
        Assert.True(File.Exists(Path.Combine(newDir, "plugin.json")));
        Assert.False(File.Exists(Path.Combine(newDir, PluginManager.PendingDeleteMarker)));
        Assert.Equal(1, secondRegistry.FetchCount);
    }

    [Fact]
    public async Task StartAsync_LeavesOldActive_WhenMarkedNewDirCannotBeRemoved()
    {
        WritePluginDir(OldDigitalId, "OldDigitalMarkedNew", "1.0.0");
        WritePluginDir(NewDigitalId, "NewDigitalMarkedNew", "2.0.0");
        var oldDir = Path.Combine(_root, OldDigitalId);
        var newDir = Path.Combine(_root, NewDigitalId);
        await File.WriteAllTextAsync(Path.Combine(newDir, PluginManager.PendingDeleteMarker), "");

        var registry = RegistryWith(NewDigitalId, "2.0.0", "unused");
        var manager = CreateManager(
            registry,
            new EmptyDownloader(),
            tryDeleteDirectory: dir => !SamePath(dir, newDir),
            options: new PluginManagerOptions
            {
                PluginRoot = _root,
                TryDeleteDirectory = dir => !SamePath(dir, newDir),
            });

        await manager.StartAsync(default);

        Assert.NotNull(manager.Find(OldDigitalId));
        Assert.Null(manager.Find(NewDigitalId));
        Assert.True(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(newDir));
        Assert.True(File.Exists(Path.Combine(newDir, PluginManager.PendingDeleteMarker)));
        Assert.False(File.Exists(Path.Combine(oldDir, PluginManager.PendingDeleteMarker)));
        Assert.Equal(0, registry.FetchCount);
    }

    [Fact]
    public async Task StartAsync_MigratesSameBoot_WhenMarkedNewDirIsDeletableByMigrator()
    {
        // Sweep failed to clear the marked new dir, but the lock has released
        // by the time the migrator runs: the migrator's own delete succeeds
        // and the full migration must complete THIS boot, not the next one.
        WritePluginDir(OldDigitalId, "OldDigitalMarkedDeletable", "1.0.0");
        WritePluginDir(NewDigitalId, "NewDigitalMarkedDeletable", "2.0.0");
        var oldDir = Path.Combine(_root, OldDigitalId);
        var newDir = Path.Combine(_root, NewDigitalId);
        await File.WriteAllTextAsync(Path.Combine(newDir, PluginManager.PendingDeleteMarker), "");

        var (zip, sha) = BuildPluginZip(NewDigitalId, "NewDigitalMarkedDeletable", "2.0.0");
        var registry = RegistryWith(NewDigitalId, "2.0.0", sha);
        var manager = CreateManager(
            registry,
            new PluginPackageDownloader(new HttpClient(new RecordingHandler { Body = zip })),
            options: new PluginManagerOptions
            {
                PluginRoot = _root,
                // Only the sweep is wedged; the migrator uses the real delete.
                TryDeleteDirectory = dir => !SamePath(dir, newDir),
            });

        await manager.StartAsync(default);

        Assert.Null(manager.Find(OldDigitalId));
        Assert.NotNull(manager.Find(NewDigitalId));
        Assert.False(Directory.Exists(oldDir));
        Assert.True(File.Exists(Path.Combine(newDir, "plugin.json")));
        Assert.False(File.Exists(Path.Combine(newDir, PluginManager.PendingDeleteMarker)));
        Assert.Equal(1, registry.FetchCount);
    }

    [Fact]
    public async Task StartAsync_DoesNotOverwriteExistingNewSettings_WhenOldDirLingers()
    {
        WritePluginDir(OldDigitalId, "OldDigitalSettings", "1.0.0");
        WritePluginDir(NewDigitalId, "NewDigitalSettings", "2.0.0");
        await _store.ForPlugin(OldDigitalId).SetAsync("profile", "old");
        await _store.ForPlugin(NewDigitalId).SetAsync("profile", "new");
        await _store.ForPlugin(NewDigitalId).SetAsync("gain", 12);

        var registry = RegistryWith(NewDigitalId, "2.0.0", "");
        var manager = CreateManager(registry, new EmptyDownloader());

        await manager.StartAsync(default);

        Assert.Null(manager.Find(OldDigitalId));
        Assert.NotNull(manager.Find(NewDigitalId));
        Assert.Equal("new", await _store.ForPlugin(NewDigitalId).GetAsync<string>("profile"));
        Assert.Equal(12, await _store.ForPlugin(NewDigitalId).GetAsync<int>("gain"));
        Assert.False(Directory.Exists(Path.Combine(_root, OldDigitalId)));
        Assert.Equal(0, registry.FetchCount);
    }

    [Fact]
    public async Task StartAsync_ActivatesOldPlugin_WhenMigrationTimesOut()
    {
        WritePluginDir(OldDigitalId, "OldDigitalTimeout", "1.0.0");
        var registry = new NeverCompletingRegistry();
        var manager = CreateManager(
            registry,
            new EmptyDownloader(),
            options: new PluginManagerOptions
            {
                PluginRoot = _root,
                MigrationTimeout = TimeSpan.FromMilliseconds(50),
            });

        var elapsed = Stopwatch.StartNew();
        await manager.StartAsync(default);
        elapsed.Stop();

        Assert.NotNull(manager.Find(OldDigitalId));
        Assert.Null(manager.Find(NewDigitalId));
        Assert.True(Directory.Exists(Path.Combine(_root, OldDigitalId)));
        Assert.Equal(1, registry.FetchCount);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StartAsync_RollsBackCopiedSettings_WhenInstallFails()
    {
        WritePluginDir(OldDigitalId, "OldDigitalBadZip", "1.0.0");
        await _store.ForPlugin(OldDigitalId).SetAsync("profile", "old-before-failure");

        var (zip, sha) = BuildZipWithoutManifest();
        var registry = RegistryWith(NewDigitalId, "3.0.0", sha);
        var manager = CreateManager(
            registry,
            new PluginPackageDownloader(new HttpClient(new RecordingHandler { Body = zip })));

        await manager.StartAsync(default);

        Assert.NotNull(manager.Find(OldDigitalId));
        Assert.Null(manager.Find(NewDigitalId));
        Assert.Equal("old-before-failure", await _store.ForPlugin(OldDigitalId).GetAsync<string>("profile"));
        Assert.Empty(_store.DumpCollection(NewDigitalId));
        Assert.Equal(1, registry.FetchCount);
    }

    [Fact]
    public async Task StartAsync_PreservesExistingNewSettings_WhenInstallFailsAfterCopySkipped()
    {
        WritePluginDir(OldDigitalId, "OldDigitalBadZipExistingSettings", "1.0.0");
        await _store.ForPlugin(OldDigitalId).SetAsync("profile", "old");
        await _store.ForPlugin(NewDigitalId).SetAsync("profile", "operator-new");

        var (zip, sha) = BuildZipWithoutManifest();
        var registry = RegistryWith(NewDigitalId, "3.0.0", sha);
        var manager = CreateManager(
            registry,
            new PluginPackageDownloader(new HttpClient(new RecordingHandler { Body = zip })));

        await manager.StartAsync(default);

        Assert.NotNull(manager.Find(OldDigitalId));
        Assert.Null(manager.Find(NewDigitalId));
        Assert.Equal("operator-new", await _store.ForPlugin(NewDigitalId).GetAsync<string>("profile"));
        Assert.Single(_store.DumpCollection(NewDigitalId));
        Assert.Equal(1, registry.FetchCount);
    }

    private PluginManager CreateManager(
        IRegistryClient registry,
        IPluginPackageDownloader downloader,
        Func<string, bool>? tryDeleteDirectory = null,
        PluginManagerOptions? options = null)
    {
        PluginManager? manager = null;
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton(downloader);
        services.AddSingleton<IRegistryClient>(registry);
        services.AddSingleton<IPluginPackageDownloader>(downloader);
        services.AddSingleton(_store);
        services.AddSingleton(_ => manager!);
        services.AddSingleton(sp => new PluginInstaller(
            downloader: sp.GetRequiredService<IPluginPackageDownloader>(),
            registry: sp.GetRequiredService<IRegistryClient>(),
            manager: manager!,
            pluginRoot: _root));
        services.AddSingleton(sp => new PluginIdMigrator(
            registry: sp.GetRequiredService<IRegistryClient>(),
            downloader: sp.GetRequiredService<IPluginPackageDownloader>(),
            settings: _store,
            pluginRoot: _root,
            log: NullLogger<PluginIdMigrator>.Instance,
            tryDeleteDirectory: tryDeleteDirectory));

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var managerOptions = options ?? new PluginManagerOptions { PluginRoot = _root };
        if (managerOptions.PluginRoot is null)
            managerOptions = managerOptions with { PluginRoot = _root };

        manager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: _store,
            services: provider,
            logFactory: NullLoggerFactory.Instance,
            options: managerOptions);
        _managers.Add(manager);
        return manager;
    }

    private static StubRegistry RegistryWith(string pluginId, string version, string sha256) =>
        new()
        {
            Catalog = new RegistryCatalog
            {
                Plugins = new[]
                {
                    new PluginEntry
                    {
                        Id = pluginId,
                        Name = "Zeus Digital",
                        Description = "Digital modes",
                        Versions = new[]
                        {
                            new PluginVersion
                            {
                                Version = version,
                                SdkAbi = 1,
                                SdkMinVersion = "1.0.0",
                                DownloadUrl = "https://example.com/digital.zip",
                                Sha256 = sha256,
                            },
                        },
                    },
                },
            },
        };

    private void WritePluginDir(string id, string asmName, string version)
    {
        using var fixture = RoslynFixture.Create(
            asmName,
            PluginSource,
            ManifestJson(id, asmName, version));

        var dest = Path.Combine(_root, PluginInstaller.SafeDirName(id));
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(fixture.PluginDir))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
    }

    private static (byte[] bytes, string sha256) BuildPluginZip(
        string id,
        string asmName,
        string version)
    {
        using var fixture = RoslynFixture.Create(
            asmName,
            PluginSource,
            ManifestJson(id, asmName, version));

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(fixture.PluginDir))
            {
                var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Fastest);
                using var es = entry.Open();
                using var fs = File.OpenRead(file);
                fs.CopyTo(es);
            }
        }

        var bytes = ms.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static (byte[] bytes, string sha256) BuildZipWithoutManifest()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("readme.txt", CompressionLevel.Fastest);
            using var es = entry.Open();
            using var writer = new StreamWriter(es);
            writer.Write("not a plugin package");
        }

        var bytes = ms.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string ManifestJson(string id, string asmName, string version) =>
        $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": "Zeus Digital",
          "version": "{{version}}",
          "description": "Digital modes",
          "sdk": { "abi": 1, "minVersion": "1.0.0" },
          "entrypoint": { "assembly": "{{asmName}}.dll" }
        }
        """;

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public byte[] Body { get; init; } = Array.Empty<byte>();
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Body),
            });
        }
    }

    private sealed class EmptyDownloader : IPluginPackageDownloader
    {
        public Task<DownloadedPluginPackage> DownloadAndVerifyToTempFileAsync(
            string url,
            string? expectedSha256,
            CancellationToken ct) =>
            throw new InvalidOperationException("download should not be reached");
    }

    private sealed class StubRegistry : IRegistryClient
    {
        public RegistryCatalog Catalog { get; init; } = new();
        public Exception? Exception { get; init; }
        public int FetchCount { get; private set; }
        public string SourceUrl => "stub://registry";

        public Task<RegistryCatalog> FetchAsync(CancellationToken ct)
        {
            FetchCount++;
            if (Exception is not null) throw Exception;
            return Task.FromResult(Catalog);
        }
    }

    private sealed class NeverCompletingRegistry : IRegistryClient
    {
        private int _fetchCount;
        public int FetchCount => _fetchCount;
        public string SourceUrl => "stub://never";

        public async Task<RegistryCatalog> FetchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _fetchCount);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new RegistryCatalog();
        }
    }
}
