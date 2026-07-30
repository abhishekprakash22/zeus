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
/// Publishes the single active audio modem to the RX/TX hot paths.
/// A plugin-hosted modem wins; the CORE modem (FreeDvModemService — the
/// in-core FreeDV backend, same story as Digital/) is the fallback when no
/// plugin modem is active, so a real org.openhpsdr.freedv plugin installed
/// alongside takes over cleanly and uninstall falls back instead of leaving
/// FreeDV dead. Extras beyond the first plugin modem are ignored with a
/// warning. The hot paths read <see cref="Current"/> with a single volatile
/// load plus one null-coalesce and do not lock.
/// </summary>
public sealed class AudioModemPluginBridge : IHostedService
{
    private readonly PluginManager _manager;
    private readonly RadioService _radio;
    private readonly Func<DspPipelineService?>? _pipelineProvider;
    private readonly IAudioModemPlugin? _coreModem;
    private readonly ILogger<AudioModemPluginBridge> _log;
    private IAudioModemPlugin? _current;
    private string? _currentId;
    private const int DeactivateTailDrainWaitMs = 1600;
    private const int DeactivateTailDrainPollMs = 20;

    public AudioModemPluginBridge(
        PluginManager manager,
        RadioService radio,
        ILogger<AudioModemPluginBridge> log,
        Func<DspPipelineService?>? pipelineProvider = null,
        IAudioModemPlugin? coreModem = null)
    {
        _manager = manager;
        _radio = radio;
        _pipelineProvider = pipelineProvider;
        _coreModem = coreModem;
        _log = log;
    }

    public IAudioModemPlugin? Current => Volatile.Read(ref _current) ?? _coreModem;

    /// <summary>
    /// Gates FREEDV mode entry in RadioService. A plugin modem gates on
    /// presence alone (unchanged semantics — its own /status carries the
    /// native story); the core modem additionally requires libcodec2 to have
    /// loaded, so a platform without the native falls back to USB instead of
    /// keying an SSB rig with raw mic audio in a mode the operator believes
    /// is digital.
    /// </summary>
    public bool HasActiveModem =>
        Volatile.Read(ref _current) is not null || _coreModem?.NativeAvailable == true;

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

        // No replacement plugin — the core modem (if runnable) takes over and
        // the operator stays in FREEDV without a mode bounce.
        if (_coreModem?.NativeAvailable == true)
        {
            _log.LogInformation("Audio modem falling back to the in-core FreeDV modem.");
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
