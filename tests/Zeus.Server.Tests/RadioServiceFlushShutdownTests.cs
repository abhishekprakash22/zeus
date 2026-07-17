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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RadioServiceFlushShutdownTests
{
    [Fact]
    public void Dispose_WithDisposedStateStoreAndThrowingLogger_DoesNotThrow()
    {
        var paths = StorePaths.Create();
        DspSettingsStore? dspStore = null;
        PaSettingsStore? paStore = null;
        RadioStateStore? stateStore = null;
        RadioService? radio = null;

        try
        {
            dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, paths.DspPath);
            paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, paths.PaPath);
            stateStore = new RadioStateStore(NullLogger<RadioStateStore>.Instance, paths.StatePath);
            radio = new RadioService(
                new ThrowingLoggerFactory(),
                dspStore,
                paStore,
                radioStateStore: stateStore);

            stateStore.Dispose();
            radio.SetWorkspaceZoom(125);

            var ex = Record.Exception(() => radio.Dispose());
            radio = null;

            Assert.Null(ex);
        }
        finally
        {
            radio?.Dispose();
            stateStore?.Dispose();
            paStore?.Dispose();
            dspStore?.Dispose();
            paths.Delete();
        }
    }

    [Fact]
    public void Dispose_WithHealthyStateStore_FlushesFinalState()
    {
        var paths = StorePaths.Create();
        DspSettingsStore? dspStore = null;
        PaSettingsStore? paStore = null;
        RadioStateStore? stateStore = null;
        RadioService? radio = null;

        try
        {
            dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, paths.DspPath);
            paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, paths.PaPath);
            stateStore = new RadioStateStore(NullLogger<RadioStateStore>.Instance, paths.StatePath);
            radio = new RadioService(
                NullLoggerFactory.Instance,
                dspStore,
                paStore,
                radioStateStore: stateStore);

            radio.SetWorkspaceZoom(135);
            radio.Dispose();
            radio = null;

            var entry = stateStore.Get();
            Assert.NotNull(entry);
            Assert.Equal(135, entry!.WorkspaceZoomPct);
        }
        finally
        {
            radio?.Dispose();
            stateStore?.Dispose();
            paStore?.Dispose();
            dspStore?.Dispose();
            paths.Delete();
        }
    }

    private sealed record StorePaths(string StatePath, string DspPath, string PaPath)
    {
        public static StorePaths Create()
        {
            var basePath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-test-{Guid.NewGuid():N}.db");
            return new StorePaths(basePath, basePath + ".dsp", basePath + ".pa");
        }

        public void Delete()
        {
            DeleteStore(StatePath);
            DeleteStore(DspPath);
            DeleteStore(PaPath);
        }

        private static void DeleteStore(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + "-log")) File.Delete(path + "-log"); } catch { }
        }
    }

    private sealed class ThrowingLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => ThrowingLogger.Instance;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public static readonly ThrowingLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new ObjectDisposedException("ThrowingLogger");
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
