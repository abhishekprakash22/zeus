// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;

namespace Zeus.Plugins.Host.Registry;

public interface IPluginPackageDownloader
{
    Task<DownloadedPluginPackage> DownloadAndVerifyToTempFileAsync(
        string url,
        string? expectedSha256,
        CancellationToken ct);
}

public sealed class DownloadedPluginPackage : IDisposable
{
    internal DownloadedPluginPackage(string path, bool deleteOnDispose = true)
    {
        Path = path;
        _deleteOnDispose = deleteOnDispose;
    }

    private readonly bool _deleteOnDispose;

    public string Path { get; }

    public void Dispose()
    {
        if (!_deleteOnDispose) return;
        try { File.Delete(Path); } catch { /* ignore */ }
    }
}

public sealed class PluginPackageDownloader : IPluginPackageDownloader
{
    private readonly HttpClient _http;

    public PluginPackageDownloader(HttpClient http) => _http = http;

    public async Task<DownloadedPluginPackage> DownloadAndVerifyToTempFileAsync(
        string url,
        string? expectedSha256,
        CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new PluginInstallException($"refusing non-HTTPS download URL: {url}");

        var tempZip = System.IO.Path.GetTempFileName();
        try
        {
            await DownloadAsync(url, tempZip, ct).ConfigureAwait(false);
            if (expectedSha256 is { Length: > 0 })
                VerifySha256(tempZip, expectedSha256);
            return new DownloadedPluginPackage(tempZip);
        }
        catch
        {
            try { File.Delete(tempZip); } catch { /* ignore */ }
            throw;
        }
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static void VerifySha256(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        var actual = Convert.ToHexString(bytes);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new PluginInstallException(
                $"sha256 mismatch — expected {expected.ToLowerInvariant()}, got {actual.ToLowerInvariant()}");
    }
}
