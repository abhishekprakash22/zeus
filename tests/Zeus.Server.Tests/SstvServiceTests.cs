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
        var dir = Path.Combine(Path.GetTempPath(), "zeus-sstv-" + Guid.NewGuid().ToString("N"));
        var digital = new DigitalService(null!, NullLogger<DigitalService>.Instance);
        var svc = new SstvService(null!, digital, NullLogger<SstvService>.Instance,
            gallery: new SstvGallery(dir));
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
            while (svc.ResetPending) await Task.Delay(5);   // restart applied, then feed
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
            Assert.True(img.Adjustable);
            Assert.NotNull(img.Key);

            // Manual slant re-renders and persists; the picture stays listed.
            var adj = svc.Adjust(img.Id, new SstvAdjustRequest(null, 500, null));
            Assert.Equal(500, adj!.SlantPpm);

            // A fresh service (restart) finds it in the gallery, as a PNG.
            var reborn = new SstvService(null!, digital, NullLogger<SstvService>.Instance,
                gallery: new SstvGallery(dir));
            await reborn.StartAsync(default);
            var back = Assert.Single(reborn.Status().Images);
            Assert.Equal(img.Key, back.Key);
            Assert.Equal(500, back.SlantPpm);
            Assert.False(back.Adjustable);
            var dto = reborn.Image(back.Id);
            Assert.NotNull(dto?.Png);
            Assert.True(reborn.Delete(back.Id));
            Assert.Empty(reborn.Status().Images);
            Assert.Empty(Directory.GetFiles(dir));
            await reborn.StopAsync(default);
        }
        finally
        {
            await svc.StopAsync(default);
            lease.Dispose();
            await pump;
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
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
