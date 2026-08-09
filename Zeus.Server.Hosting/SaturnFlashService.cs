// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// SaturnFlashService — in-app FPGA firmware update for the ANAN G2 / G2
// Ultra: a C# port of Laurence Barker G8NJJ's flashwriter
// (Saturn/sw_tools/flashwriter, GPL like us) — the AXI Quad SPI polled
// driver (Xilinx xspi_l.h register map) with register I/O redirected to
// pread/pwrite on /dev/xdma0_user, and the S25FL 4-byte-address command
// layer on top.
//
// The two facts that make this safe by design:
//   * Flash layout: FALLBACK (golden) image at 0x000000, PRIMARY at
//     0x980000. Zeus writes ONLY the primary slot — a bad primary falls
//     back to golden at configuration load. The golden slot is never
//     offered, never addressed, never touched.
//   * The SPI controller lives at base 0x10000 inside the same XDMA user
//     BAR the SaturnXdmaProbe already validates — no new device access.
//
// Flow (flashwriter's, verbatim in spirit): download .bin → erase the
// covered sectors (0xDC, 4-byte) → page-program 256-byte pages (0x12) →
// read back (0x13) and compare → report. Progress is polled via
// /api/fpga/flash. Guards: XDMA Saturn present, not while TX keyed, one
// job at a time, typed confirmation enforced by the UI (Phase B).

using Zeus.Contracts;

namespace Zeus.Server;

public sealed class SaturnFlashService
{
    // ---- AXI Quad SPI (xspi_l.h) at 0x10000 in the XDMA user BAR ----
    private const long SpiBase = 0x10000;
    private const long SRR = SpiBase + 0x40;
    private const long CR = SpiBase + 0x60;
    private const long SR = SpiBase + 0x64;
    private const long DTR = SpiBase + 0x68;
    private const long DRR = SpiBase + 0x6C;
    private const long SSR = SpiBase + 0x70;
    private const uint SrrReset = 0x0000000A;
    private const uint CrEnable = 0x2, CrMaster = 0x4, CrTxReset = 0x20,
        CrRxReset = 0x40, CrManualSs = 0x80, CrTransInhibit = 0x100;
    private const uint SrRxEmpty = 0x1, SrTxFull = 0x8;

    // ---- S25FL command layer (spi-s25fl.hpp) ----
    private const byte CmdRead4 = 0x13, CmdPageProgram4 = 0x12,
        CmdWriteEnable = 0x06, CmdSectorErase4 = 0xDC, CmdReadStatus = 0x05;
    private const int PageBytes = 256;
    private const int SectorBytes = 64 * 1024;
    private const uint PrimaryAddr = 0x00980000;   // VPRIMARYADDR — the only slot Zeus writes

    private const string UserDev = "/dev/xdma0_user";

    private readonly TxService _tx;
    private readonly SaturnXdmaProbe _probe;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SaturnFlashService> _log;

    private readonly object _lock = new();
    private string _phase = "idle";     // idle|downloading|erasing|writing|verifying|done|error
    private double _progress;           // 0..1 within the phase
    private string _detail = "";
    private string? _error;

    public SaturnFlashService(
        TxService tx, SaturnXdmaProbe probe,
        IHttpClientFactory http, ILogger<SaturnFlashService> log)
    {
        _tx = tx;
        _probe = probe;
        _http = http;
        _log = log;
    }

    /// <summary>Compare the primary slot's first 4 KB against the first
    /// 4 KB of a shelf bitstream (ranged GET). Xilinx bitstreams carry their
    /// build identity in the header, so a byte-exact head match means "this
    /// image is what's in the primary slot" without filename heuristics.
    /// Also reports the RUNNING gateware version from the identity registers
    /// — which reflects the flash only after a power-cycle.</summary>
    public async Task<object> CompareAsync(string url)
    {
        var probe = _probe.Probe();
        if (!OperatingSystem.IsLinux() || probe is null)
            return new { ok = false, error = "no Saturn on the local PCIe bus" };
        lock (_lock)
        {
            if (_phase is not ("idle" or "done" or "error"))
                return new { ok = false, error = "a flash job is running" };
        }
        const int HeadBytes = 4096;
        byte[] shelfHead;
        using (var client = _http.CreateClient())
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, HeadBytes - 1);
            using var resp = await client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            shelfHead = await resp.Content.ReadAsByteArrayAsync();
        }
        var flashHead = new byte[Math.Min(HeadBytes, shelfHead.Length)];
        using (var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            SpiInit(fs);
            Command(fs, CmdRead4, PrimaryAddr, ReadOnlySpan<byte>.Empty, flashHead.Length, flashHead);
        }
        bool match = flashHead.AsSpan().SequenceEqual(shelfHead.AsSpan(0, flashHead.Length));
        bool blank = flashHead.All(b => b == 0xFF);
        return new
        {
            ok = true,
            match,
            primaryBlank = blank,
            runningVersion = probe.MajorVersion > 0
                ? $"{probe.MajorVersion}.{probe.MinorVersion}"
                : $"0.{probe.MinorVersion}",
            userVersion = probe.UserVersion,
            note = match
                ? "this image is already in the primary slot"
                : blank
                    ? "primary slot is blank"
                    : "primary slot holds a different image",
        };
    }

    public object Status()
    {
        lock (_lock)
        {
            return new
            {
                phase = _phase,
                progress = _progress,
                detail = _detail,
                error = _error,
                primaryAddrHex = $"0x{PrimaryAddr:X6}",
                saturnPresent = _probe.Probe() is not null,
            };
        }
    }

    /// <summary>Start a primary-slot flash from a bitstream URL (the Saturn
    /// repo's FPGA folder in Phase B's UI). Refuses without an XDMA Saturn,
    /// while TX is keyed, or while a job runs.</summary>
    public bool Start(string url, out string? refusal)
    {
        refusal = null;
        if (!OperatingSystem.IsLinux() || _probe.Probe() is null)
        { refusal = "no Saturn on the local PCIe bus — FPGA update runs on the radio itself"; return false; }
        if (_tx.MoxOwner is not null)
        { refusal = "TX is keyed — unkey before flashing"; return false; }
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        { refusal = "bitstream URL must be https"; return false; }
        lock (_lock)
        {
            if (_phase is not ("idle" or "done" or "error"))
            { refusal = "a flash job is already running"; return false; }
            _phase = "downloading"; _progress = 0; _detail = url; _error = null;
        }
        _ = Task.Run(() => RunJob(url));
        return true;
    }

    private async Task RunJob(string url)
    {
        try
        {
            // 1. Download the bitstream in full before touching hardware.
            byte[] image;
            using (var client = _http.CreateClient())
            {
                client.Timeout = TimeSpan.FromMinutes(5);
                image = await client.GetByteArrayAsync(url);
            }
            if (image.Length < 1024 * 1024 || image.Length > 32 * 1024 * 1024)
                throw new InvalidOperationException($"bitstream size {image.Length} is implausible for a Saturn image");
            _log.LogWarning("fpga: flashing PRIMARY slot with {Bytes} bytes from {Url}", image.Length, url);

            using var fs = new FileStream(UserDev, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            SpiInit(fs);

            // 2. Erase the covered sectors of the PRIMARY slot only.
            SetPhase("erasing", 0, "");
            uint end = PrimaryAddr + (uint)image.Length;
            int sectors = (int)((end - PrimaryAddr + SectorBytes - 1) / SectorBytes);
            for (int i = 0; i < sectors; i++)
            {
                uint addr = PrimaryAddr + (uint)(i * SectorBytes);
                WriteEnable(fs);
                Command(fs, CmdSectorErase4, addr, ReadOnlySpan<byte>.Empty, 0);
                WaitFlashReady(fs, 4.0);
                SetPhase("erasing", (i + 1) / (double)sectors, $"sector {i + 1}/{sectors}");
            }

            // 3. Page-program.
            SetPhase("writing", 0, "");
            for (int off = 0; off < image.Length; off += PageBytes)
            {
                int n = Math.Min(PageBytes, image.Length - off);
                WriteEnable(fs);
                Command(fs, CmdPageProgram4, PrimaryAddr + (uint)off, image.AsSpan(off, n), 0);
                WaitFlashReady(fs, 0.5);
                if ((off / PageBytes) % 64 == 0)
                    SetPhase("writing", off / (double)image.Length, $"{off / 1024} / {image.Length / 1024} kB");
            }

            // 4. Verify by readback — success is measured, not assumed.
            SetPhase("verifying", 0, "");
            var page = new byte[PageBytes];
            for (int off = 0; off < image.Length; off += PageBytes)
            {
                int n = Math.Min(PageBytes, image.Length - off);
                Command(fs, CmdRead4, PrimaryAddr + (uint)off, ReadOnlySpan<byte>.Empty, n, page);
                if (!page.AsSpan(0, n).SequenceEqual(image.AsSpan(off, n)))
                    throw new InvalidOperationException($"verify mismatch at 0x{PrimaryAddr + (uint)off:X}");
                if ((off / PageBytes) % 64 == 0)
                    SetPhase("verifying", off / (double)image.Length, $"{off / 1024} / {image.Length / 1024} kB");
            }

            SetPhase("done", 1,
                "primary image written and verified — power-cycle the radio to load it (golden fallback remains untouched)");
            _log.LogWarning("fpga: primary flash verified OK ({Bytes} bytes)", image.Length);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "fpga: flash job failed");
            lock (_lock) { _phase = "error"; _error = ex.Message; }
        }
    }

    private void SetPhase(string phase, double progress, string detail)
    {
        lock (_lock) { _phase = phase; _progress = progress; _detail = detail; }
    }

    // ---- SPI plumbing: xspi polled mode over pread/pwrite ----

    private static void Reg(FileStream fs, long off, uint val)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, val);
        RandomAccess.Write(fs.SafeFileHandle, b, off);
    }

    private static uint Reg(FileStream fs, long off)
    {
        Span<byte> b = stackalloc byte[4];
        return RandomAccess.Read(fs.SafeFileHandle, b, off) == 4 ? BitConverter.ToUInt32(b) : 0;
    }

    private static void SpiInit(FileStream fs)
    {
        Reg(fs, SRR, SrrReset);
        Reg(fs, CR, CrEnable | CrMaster | CrManualSs | CrTxReset | CrRxReset | CrTransInhibit);
        Reg(fs, SSR, 0xFFFFFFFF);
    }

    /// <summary>One SPI transaction: cmd + 4-byte address (when addressed) +
    /// payload out, optionally reading <paramref name="readCount"/> bytes
    /// back into <paramref name="readInto"/>. Manual slave select frames the
    /// whole exchange; bytes clock through the FIFOs in polled mode.</summary>
    private static void Command(
        FileStream fs, byte cmd, uint? addr, ReadOnlySpan<byte> payload,
        int readCount, byte[]? readInto = null)
    {
        int header = 1 + (addr is null ? 0 : 4);
        int total = header + payload.Length + readCount;
        Span<byte> txPrefix = stackalloc byte[5];
        txPrefix[0] = cmd;
        if (addr is uint a)
        {
            txPrefix[1] = (byte)(a >> 24);
            txPrefix[2] = (byte)(a >> 16);
            txPrefix[3] = (byte)(a >> 8);
            txPrefix[4] = (byte)a;
        }

        Reg(fs, SSR, 0xFFFFFFFE);                       // select slave 0
        uint cr = Reg(fs, CR);
        Reg(fs, CR, cr & ~CrTransInhibit);              // release the master

        int sent = 0, received = 0;
        while (received < total)
        {
            while (sent < total && (Reg(fs, SR) & SrTxFull) == 0)
            {
                byte b = sent < header ? txPrefix[sent]
                    : sent < header + payload.Length ? payload[sent - header]
                    : (byte)0xFF;                        // dummy clocks for reads
                Reg(fs, DTR, b);
                sent++;
            }
            while ((Reg(fs, SR) & SrRxEmpty) == 0)
            {
                byte b = (byte)Reg(fs, DRR);
                int dataIdx = received - header - payload.Length;
                if (dataIdx >= 0 && readInto is not null && dataIdx < readCount)
                    readInto[dataIdx] = b;
                received++;
            }
        }

        Reg(fs, CR, cr | CrTransInhibit);
        Reg(fs, SSR, 0xFFFFFFFF);                       // deselect
    }

    private static void Command(FileStream fs, byte cmd, uint addr, ReadOnlySpan<byte> payload, int readCount)
        => Command(fs, cmd, (uint?)addr, payload, readCount);

    private static void WriteEnable(FileStream fs)
        => Command(fs, CmdWriteEnable, null, ReadOnlySpan<byte>.Empty, 0);

    private static void WaitFlashReady(FileStream fs, double timeoutS)
    {
        var one = new byte[1];
        long deadline = Environment.TickCount64 + (long)(timeoutS * 1000);
        while (Environment.TickCount64 < deadline)
        {
            Command(fs, CmdReadStatus, null, ReadOnlySpan<byte>.Empty, 1, one);
            if ((one[0] & 0x01) == 0) return;           // WIP clear
            Thread.Sleep(2);
        }
        throw new TimeoutException("flash stayed busy past its deadline");
    }
}
