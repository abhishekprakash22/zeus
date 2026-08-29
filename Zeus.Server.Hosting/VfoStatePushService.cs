// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// VfoStatePushService — bridges RadioService.StateChanged to the /ws
// VfoStateFrame (0x3B) so every client's VFO numerals track NON-self-
// originated tuning at display rate. Without it, the web UI learns such
// changes only from the 1 Hz App.tsx poll: a G2 front-panel spin showed the
// dial stepping once a second while the spectrum (stamping its own CenterHz)
// scrolled live underneath. piHPSDR redraws its dial per step in-process;
// this is the wire equivalent.
//
// Cadence: coalesced to MinIntervalMs (~30 Hz) with a GUARANTEED trailing
// send — a change inside the quiet window arms a one-shot timer for the
// window's remainder, so the FINAL frequency always lands even when the
// knob stops mid-window. Frames only flow while the VFO is actually
// changing; a quiet dial costs zero wire. StateChanged can fire from
// several threads (panel flush timer, HTTP handlers, TCI), so the tiny
// state block is lock-guarded; the hub enqueue is non-blocking drop-oldest
// per client, safe to call under the lock's shadow but done outside it.

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class VfoStatePushService : IHostedService, IDisposable
{
    private const int MinIntervalMs = 33; // ~30 Hz — display rate, 17 B/frame

    private readonly RadioService _radio;
    private readonly StreamingHub _hub;
    private readonly ILogger<VfoStatePushService> _log;

    private readonly object _sync = new();
    private readonly Timer _trailing;
    private long _lastSentA = long.MinValue;
    private long _lastSentB = long.MinValue;
    private long _lastSentTickMs;
    private long _pendingA;
    private long _pendingB;
    private bool _trailingArmed;

    public VfoStatePushService(RadioService radio, StreamingHub hub, ILogger<VfoStatePushService> log)
    {
        _radio = radio;
        _hub = hub;
        _log = log;
        _trailing = new Timer(OnTrailing, null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _radio.StateChanged += OnStateChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _radio.StateChanged -= OnStateChanged;
        lock (_sync)
        {
            _trailingArmed = false;
            _trailing.Change(Timeout.Infinite, Timeout.Infinite);
        }
        return Task.CompletedTask;
    }

    private void OnStateChanged(StateDto s)
    {
        long a = s.VfoHz;
        long b = s.Rx2().VfoHz;
        bool sendNow = false;

        lock (_sync)
        {
            if (a == _lastSentA && b == _lastSentB && !_trailingArmed) return;
            _pendingA = a;
            _pendingB = b;

            long now = Environment.TickCount64;
            if (now - _lastSentTickMs >= MinIntervalMs)
            {
                _lastSentA = a;
                _lastSentB = b;
                _lastSentTickMs = now;
                if (_trailingArmed)
                {
                    _trailingArmed = false;
                    _trailing.Change(Timeout.Infinite, Timeout.Infinite);
                }
                sendNow = true;
            }
            else if (!_trailingArmed)
            {
                // Inside the quiet window: arm ONE trailing shot for its
                // remainder so the latest value always lands.
                _trailingArmed = true;
                _trailing.Change(Math.Max(1, MinIntervalMs - (now - _lastSentTickMs)), Timeout.Infinite);
            }
        }

        if (sendNow) _hub.Broadcast(new VfoStateFrame(a, b));
    }

    private void OnTrailing(object? _)
    {
        long a, b;
        lock (_sync)
        {
            if (!_trailingArmed) return;
            _trailingArmed = false;
            a = _pendingA;
            b = _pendingB;
            if (a == _lastSentA && b == _lastSentB) return;
            _lastSentA = a;
            _lastSentB = b;
            _lastSentTickMs = Environment.TickCount64;
        }
        try
        {
            _hub.Broadcast(new VfoStateFrame(a, b));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "vfo.push.trailing.error");
        }
    }

    public void Dispose() => _trailing.Dispose();
}
