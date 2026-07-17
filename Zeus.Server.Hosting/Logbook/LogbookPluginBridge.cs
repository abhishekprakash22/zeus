// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts.Extensions;
using Zeus.Plugins.Host;

namespace Zeus.Server;

/// <summary>
/// Publishes the single active logbook backend plugin to the core /api/log/*
/// handlers. First active logbook wins; live install/uninstall follows the
/// PluginManager activation events without requiring a host restart.
/// </summary>
public sealed class LogbookPluginBridge : IHostedService
{
    private readonly PluginManager? _manager;
    private readonly ILogger<LogbookPluginBridge> _log;
    private readonly object _gate = new();
    private ILogbookPlugin? _current;
    private string? _currentId;

    public LogbookPluginBridge(PluginManager manager, ILogger<LogbookPluginBridge> log)
    {
        _manager = manager;
        _log = log;
    }

    internal LogbookPluginBridge(ILogger<LogbookPluginBridge> log)
    {
        _log = log;
    }

    public ILogbookPlugin? Current => Volatile.Read(ref _current);

    public Task StartAsync(CancellationToken ct)
    {
        if (_manager is null) return Task.CompletedTask;
        _manager.PluginActivated += OnPluginActivated;
        _manager.PluginDeactivated += OnPluginDeactivated;
        foreach (var p in _manager.Active) OnPluginActivated(p);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_manager is not null)
        {
            _manager.PluginActivated -= OnPluginActivated;
            _manager.PluginDeactivated -= OnPluginDeactivated;
        }
        lock (_gate)
        {
            Volatile.Write(ref _current, null);
            _currentId = null;
        }
        return Task.CompletedTask;
    }

    public void Attach(ILogbookPlugin plugin) => Attach(plugin, plugin.GetType().Name);

    public void Detach(ILogbookPlugin plugin)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(Current, plugin)) return;
            var inactiveId = _currentId;
            Volatile.Write(ref _current, null);
            _currentId = null;
            _log.LogInformation("Logbook plugin {Id} inactive.", inactiveId ?? "(manual)");
        }
    }

    private void OnPluginActivated(ActivatedPlugin p)
    {
        if (p.Loaded.Plugin is ILogbookPlugin logbook)
            Attach(logbook, p.Loaded.Manifest.Id);
    }

    private void OnPluginDeactivated(ActivatedPlugin p)
    {
        if (p.Loaded.Plugin is not ILogbookPlugin logbook) return;
        Detach(logbook);

        if (_manager is null) return;
        foreach (var active in _manager.Active)
        {
            if (ReferenceEquals(active.Loaded.Plugin, p.Loaded.Plugin)) continue;
            if (active.Loaded.Plugin is ILogbookPlugin replacement)
            {
                Attach(replacement, active.Loaded.Manifest.Id);
                return;
            }
        }
    }

    private void Attach(ILogbookPlugin plugin, string? id)
    {
        lock (_gate)
        {
            var existing = Current;
            if (ReferenceEquals(existing, plugin)) return;
            if (existing is not null)
            {
                _log.LogWarning(
                    "Ignoring logbook plugin {Id}; {ExistingId} is already active.",
                    id ?? "(unknown)",
                    _currentId ?? "(unknown)");
                return;
            }

            _currentId = id;
            Volatile.Write(ref _current, plugin);
            _log.LogInformation("Logbook plugin {Id} active.", _currentId ?? "(manual)");
        }
    }
}
