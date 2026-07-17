using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class AudioPluginBridgeRxRouteTests
{
    [Fact]
    public void ReapplyRxSlots_RoutesRxVstOnlyThroughRxEngine_WhenEngineAvailable()
    {
        using var engineEnv = VstEnginePathOverride.Create();
        using var fixture = new RxVstFixture();
        var bridge = NewBridge(fixture.Service);
        var plugin = new VstHostAudioPlugin(
            new NoopVstBridge(),
            new AudioBlock { Vst3Path = "FakeRx.vst3", Slot = "rx.post-demod" },
            Path.GetTempPath(),
            "Fake RX VST");
        const string pluginId = "com.openhpsdr.zeus.rxvst.fake";
        SeedRxPlugin(bridge, pluginId, plugin);

        var nativeActive = ReapplyRxSlots(bridge, [pluginId], out var engineRouteActive);

        Assert.False(nativeActive);
        Assert.True(engineRouteActive);
        Assert.False(RxSlotMap(bridge).ContainsKey(pluginId));
        Assert.Null(RxChain(bridge).GetSlot(0));
    }

    [Fact]
    public void ReapplyRxSlots_KeepsNativeRxPluginsInNativeRxChain()
    {
        using var engineEnv = VstEnginePathOverride.Create();
        using var fixture = new RxVstFixture();
        var bridge = NewBridge(fixture.Service);
        var plugin = new SpyRxPlugin();
        const string pluginId = "com.openhpsdr.zeus.rx.native";
        SeedRxPlugin(bridge, pluginId, plugin);

        var nativeActive = ReapplyRxSlots(bridge, [pluginId], out var engineRouteActive);

        Assert.True(nativeActive);
        Assert.False(engineRouteActive);
        Assert.True(RxSlotMap(bridge).TryGetValue(pluginId, out var slot));
        Assert.Equal(0, slot);
        Assert.Same(plugin, RxChain(bridge).GetSlot(0));
    }

    [Fact]
    public void ProcessRxBlock_UpdatesReceiveChainMeters()
    {
        using var fixture = new RxVstFixture();
        var bridge = NewBridge(fixture.Service);
        var plugin = new GainRxPlugin(0.25f);
        const string pluginId = "com.openhpsdr.zeus.rx.native.gain";
        SeedRxPlugin(bridge, pluginId, plugin);

        var nativeActive = ReapplyRxSlots(bridge, [pluginId], out var engineRouteActive);
        var audio = new[] { 0.20f, -0.50f, 0.10f, -0.25f };

        bridge.ProcessRxForTest(audio, audio.Length, 48_000);

        var meters = bridge.RxChainMeters;
        Assert.True(nativeActive);
        Assert.False(engineRouteActive);
        Assert.Equal(0.50f, meters.In, precision: 3);
        Assert.Equal(0.125f, meters.Out, precision: 3);
        Assert.Equal(0.05f, audio[0], precision: 3);
        Assert.Equal(-0.125f, audio[1], precision: 3);
    }

    private static AudioPluginBridge NewBridge(RxVstEngineService rxVstEngine) =>
        new(
            isMoxOn: () => false,
            isMonitorOn: () => false,
            log: NullLogger<AudioPluginBridge>.Instance,
            rxVstEngine: rxVstEngine);

    private static void SeedRxPlugin(AudioPluginBridge bridge, string id, IAudioPlugin plugin)
    {
        RxPluginMap(bridge)[id] = plugin;
        RxInitializedSet(bridge).Add(id);
    }

    private static bool ReapplyRxSlots(
        AudioPluginBridge bridge,
        IReadOnlyList<string> activeOrder,
        out bool engineRouteActive)
    {
        var method = typeof(AudioPluginBridge).GetMethod(
            "ReapplyRxSlotsUnderLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] args = [activeOrder, false];
        var nativeActive = (bool)method.Invoke(bridge, args)!;
        engineRouteActive = (bool)args[1]!;
        return nativeActive;
    }

    private static Dictionary<string, IAudioPlugin> RxPluginMap(AudioPluginBridge bridge) =>
        PrivateField<Dictionary<string, IAudioPlugin>>(bridge, "_rxIdToPlugin");

    private static Dictionary<string, int> RxSlotMap(AudioPluginBridge bridge) =>
        PrivateField<Dictionary<string, int>>(bridge, "_rxIdToSlot");

    private static HashSet<string> RxInitializedSet(AudioPluginBridge bridge) =>
        PrivateField<HashSet<string>>(bridge, "_rxInitializedIds");

    private static AudioChain RxChain(AudioPluginBridge bridge) =>
        PrivateField<AudioChain>(bridge, "_rxChain");

    private static T PrivateField<T>(object target, string name)
    {
        var field = target.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(target));
    }

    private sealed class RxVstFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "zeus-rx-vst-route-" + Guid.NewGuid().ToString("N"));
        private readonly PluginSettingsStore _settings;
        private readonly PluginManager _manager;
        private readonly RxChainOrderStore _store;

        public RxVstFixture()
        {
            Directory.CreateDirectory(_root);
            _settings = new PluginSettingsStore(Path.Combine(_root, "settings.db"));
            _manager = new PluginManager(
                loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
                settings: _settings,
                services: new ServiceCollection().BuildServiceProvider(),
                logFactory: NullLoggerFactory.Instance,
                options: new PluginManagerOptions { PluginRoot = Path.Combine(_root, "plugins") });
            _store = new RxChainOrderStore(
                NullLogger<RxChainOrderStore>.Instance,
                Path.Combine(_root, "rx-chain.db"));
            var chainOrder = new RxChainOrderService(
                _store,
                NullLogger<RxChainOrderService>.Instance);
            Service = new RxVstEngineService(
                _manager,
                chainOrder,
                new VstEngineController(),
                NullLogger<RxVstEngineService>.Instance);
        }

        public RxVstEngineService Service { get; }

        public void Dispose()
        {
            Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _settings.Dispose();
            _store.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private sealed class VstEnginePathOverride : IDisposable
    {
        private readonly string? _previous;
        private readonly string _path;

        private VstEnginePathOverride(string path, string? previous)
        {
            _path = path;
            _previous = previous;
        }

        public static VstEnginePathOverride Create()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "zeus-fake-vst-engine-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllBytes(path, []);
            var previous = Environment.GetEnvironmentVariable("ZEUS_VST_ENGINE_PATH");
            Environment.SetEnvironmentVariable("ZEUS_VST_ENGINE_PATH", path);
            return new VstEnginePathOverride(path, previous);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("ZEUS_VST_ENGINE_PATH", _previous);
            try { File.Delete(_path); } catch { }
        }
    }

    private sealed class SpyRxPlugin : IAudioPlugin
    {
        public string DisplayName => "Spy RX";
        public AudioPluginRequirements Requirements => new(48_000, 1, 2_048);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;
        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx) =>
            input.CopyTo(output);
        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class GainRxPlugin(float gain) : IAudioPlugin
    {
        public string DisplayName => "Gain RX";
        public AudioPluginRequirements Requirements => new(48_000, 1, 2_048);
        public Task InitializeAudioAsync(IAudioHost host, CancellationToken ct) => Task.CompletedTask;

        public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            for (int i = 0; i < input.Length; i++)
                output[i] = input[i] * gain;
        }

        public Task ShutdownAudioAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopVstBridge : IVstBridgeNative
    {
        public int Init(int abi) => VstBridgeStatus.Ok;
        public int LoadVst3(string path, int channels, int sampleRate, int blockSize, out nint handle)
        {
            handle = 0;
            return VstBridgeStatus.Ok;
        }

        public int Process(nint handle, ReadOnlySpan<float> input, Span<float> output, int frames)
        {
            input.CopyTo(output);
            return VstBridgeStatus.Ok;
        }

        public int SetParameter(nint handle, uint paramId, double normalized) => VstBridgeStatus.Ok;
        public int Unload(nint handle) => VstBridgeStatus.Ok;
        public int Shutdown() => VstBridgeStatus.Ok;
        public int EditorOpen(nint handle, string title) => VstBridgeStatus.Ok;
        public int EditorClose(nint handle) => VstBridgeStatus.Ok;
        public bool EditorIsOpen(nint handle) => false;
    }
}
