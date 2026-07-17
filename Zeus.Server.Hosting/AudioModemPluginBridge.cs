// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host;

namespace Zeus.Server;

/// <summary>
/// Publishes the single active plugin audio modem to the RX/TX hot paths.
/// First active modem wins; extras are ignored with a warning. The hot paths
/// read <see cref="Current"/> with a single volatile load and do not lock.
/// </summary>
public sealed class AudioModemPluginBridge : IHostedService
{
    private readonly PluginManager _manager;
    private readonly RadioService _radio;
    private readonly Func<DspPipelineService?>? _pipelineProvider;
    private readonly ILogger<AudioModemPluginBridge> _log;
    private IAudioModemPlugin? _current;
    private string? _currentId;
    private const int DeactivateTailDrainWaitMs = 1600;
    private const int DeactivateTailDrainPollMs = 20;

    public AudioModemPluginBridge(
        PluginManager manager,
        RadioService radio,
        ILogger<AudioModemPluginBridge> log,
        Func<DspPipelineService?>? pipelineProvider = null)
    {
        _manager = manager;
        _radio = radio;
        _pipelineProvider = pipelineProvider;
        _log = log;
    }

    public IAudioModemPlugin? Current => Volatile.Read(ref _current);

    public bool HasActiveModem => Current is not null;

    public Task StartAsync(CancellationToken ct)
    {
        _radio.SetModemAvailability(() => HasActiveModem);
        _manager.PluginActivated += OnPluginActivated;
        _manager.PluginDeactivated += OnPluginDeactivated;
        foreach (var p in _manager.Active) OnPluginActivated(p);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _manager.PluginActivated -= OnPluginActivated;
        _manager.PluginDeactivated -= OnPluginDeactivated;
        Volatile.Write(ref _current, null);
        _currentId = null;
        _radio.SetModemAvailability(null);
        return Task.CompletedTask;
    }

    private void OnPluginActivated(ActivatedPlugin p)
    {
        if (p.Loaded.Plugin is not IAudioModemPlugin modem) return;

        var existing = Current;
        if (existing is not null)
        {
            _log.LogWarning(
                "Ignoring audio modem plugin {Id}; {ExistingId} is already active.",
                p.Loaded.Manifest.Id, _currentId ?? "(unknown)");
            return;
        }

        _currentId = p.Loaded.Manifest.Id;
        Volatile.Write(ref _current, modem);
        _log.LogInformation("Audio modem plugin {Id} active.", _currentId);
    }

    private void OnPluginDeactivated(ActivatedPlugin p)
    {
        if (p.Loaded.Plugin is not IAudioModemPlugin modem) return;
        if (!ReferenceEquals(Current, modem)) return;

        var inactiveId = _currentId;
        Volatile.Write(ref _current, null);
        _log.LogInformation("Audio modem plugin {Id} inactive.", inactiveId);
        _currentId = null;

        DisengageModemAfterTailDrain(
            modem,
            () => _pipelineProvider?.Invoke()?.IsFreeDvTailDraining == true,
            _log,
            inactiveId,
            DeactivateTailDrainWaitMs,
            DeactivateTailDrainPollMs);

        foreach (var active in _manager.Active)
        {
            if (ReferenceEquals(active.Loaded.Plugin, p.Loaded.Plugin)) continue;
            if (active.Loaded.Plugin is not IAudioModemPlugin replacement) continue;

            _currentId = active.Loaded.Manifest.Id;
            Volatile.Write(ref _current, replacement);
            _log.LogInformation("Audio modem plugin {Id} active.", _currentId);
            return;
        }

        try
        {
            var state = _radio.Snapshot();
            if (state.Mode == RxMode.FreeDv)
                _radio.SetMode(RxMode.USB, TxVfo.A);
            if (state.Rx2Enabled && state.Rx2().Mode == RxMode.FreeDv)
                _radio.SetMode(RxMode.USB, TxVfo.B);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not leave FreeDV mode after modem plugin deactivation.");
        }
    }

    internal static bool DisengageModemAfterTailDrain(
        IAudioModemPlugin modem,
        Func<bool> isTailDraining,
        ILogger log,
        string? pluginId,
        int timeoutMs = DeactivateTailDrainWaitMs,
        int pollMs = DeactivateTailDrainPollMs)
    {
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (isTailDraining())
        {
            if (Environment.TickCount64 >= deadline)
            {
                log.LogWarning(
                    "Audio modem plugin {Id} deactivation timed out waiting for FreeDV TX tail drain; skipping modem flush.",
                    pluginId ?? "(unknown)");
                return false;
            }
            Thread.Sleep(Math.Max(1, pollMs));
        }

        try
        {
            modem.SyncMode(0);
            modem.FlushRx();
            modem.FlushTx();
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Audio modem plugin {Id} disengage threw during deactivation.", pluginId);
            return false;
        }
    }
}
