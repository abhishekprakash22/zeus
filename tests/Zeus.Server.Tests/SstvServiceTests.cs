// SPDX-License-Identifier: GPL-2.0-or-later
//
// SstvService end to end: 48 kHz RX audio in (as DspPipelineService delivers
// it), through the ring and worker thread, out as `sstv` SSE frames and a
// finished picture in /sstv status.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Digital;
using Zeus.Server.Hosting.Digital.Sstv;

namespace Zeus.Server.Tests;

public sealed class SstvServiceTests
{
    [Fact]
    public async Task Pd50_FromRxAudio_StreamsRowsAndFinishesThePicture()
    {
        var digital = new DigitalService(null!, NullLogger<DigitalService>.Instance);
        var svc = new SstvService(null!, digital, NullLogger<SstvService>.Instance);
        var (reader, lease) = digital.Events.Subscribe();
        var frames = new List<string>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in reader.ReadAllAsync()) lock (frames) frames.Add(f);
        });

        await svc.StartAsync(default);
        try
        {
            svc.Enable(0);
            var mode = SstvModes.PD50;
            var rgb = new byte[mode.Width * mode.Height * 3];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 7);
            var audio = SstvEncoder.Encode(mode, rgb, 48_000, 0.5f);
            var padded = new float[48_000 + audio.Length + 48_000];
            audio.CopyTo(padded, 48_000);

            // Faster than real time, but paced by the worker: never let the
            // backlog near the ring size, however slow a loaded CI box makes
            // the decode thread (an overrun is the service's designed
            // response to a stall, not what this test is about).
            const int block = 1024;
            for (int i = 0; i < padded.Length; i += block)
            {
                svc.FeedRxAudio(0, 48_000,
                    padded.AsMemory(i, Math.Min(block, padded.Length - i)));
                while (svc.Backlog > 3 * 12_000) await Task.Delay(5);
            }

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (svc.Status().Images.Length == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            var st = svc.Status();
            var img = Assert.Single(st.Images);
            Assert.Equal("PD 50", img.Mode);
            Assert.Equal("Complete", img.EndReason);
            Assert.Equal(mode.Height, img.RowsDone);
            Assert.NotNull(svc.Image(img.Id));
        }
        finally
        {
            await svc.StopAsync(default);
            lease.Dispose();
            await pump;
        }

        lock (frames)
        {
            Assert.Contains(frames, f => f.StartsWith("event: sstv") && f.Contains("\"kind\":\"start\""));
            Assert.Contains(frames, f => f.Contains("\"kind\":\"end\""));
            // The hub is bounded (DropOldest), so not every row frame need
            // survive a burst this fast — but rows must flow.
            Assert.True(frames.Count(f => f.Contains("\"kind\":\"rows\"")) > 10);
        }
    }
}
