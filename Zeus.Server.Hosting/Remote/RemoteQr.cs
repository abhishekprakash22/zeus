using QRCoder;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Builds the operator's canonical remote address and renders it as a scannable
/// QR code for the Server menu (ADR-0006/0007). The QR encodes the
/// <c>/go/&lt;callsign&gt;</c> URL so a phone camera opens the remote client
/// directly — no typing a URL.
/// </summary>
public static class RemoteQr
{
    /// <summary>
    /// Origin used to build the operator's canonical remote address. Upstream
    /// hardcoded this to the project's own site; it is configurable here so a
    /// self-hosted deployment can point at its own remote client / broker origin
    /// (see cloud/zeus-remote-broker/). Set ZEUS_REMOTE_ORIGIN, e.g.
    /// <c>https://remote.example.com</c>. Falls back to the upstream default.
    /// </summary>
    public static string BrokerOrigin
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("ZEUS_REMOTE_ORIGIN")?.Trim();
            return string.IsNullOrEmpty(configured)
                ? DefaultBrokerOrigin
                : configured.TrimEnd('/');
        }
    }

    public const string DefaultBrokerOrigin = "https://openhpsdrzeus.com";

    /// <summary>
    /// Canonical remote address for a callsign, e.g.
    /// <c>https://openhpsdrzeus.com/go/EI6LF</c>. The callsign is uppercased and
    /// URL-escaped so portable/special calls (<c>EI6LF/P</c>) stay a single path
    /// segment. Returns null when no callsign is available.
    /// </summary>
    public static string? AddressFor(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return null;
        var normalized = callsign.Trim().ToUpperInvariant();
        return $"{BrokerOrigin}/go/{Uri.EscapeDataString(normalized)}";
    }

    /// <summary>
    /// Render text (a URL) to a standalone SVG QR code. SVG keeps it crisp at any
    /// size and needs no raster/System.Drawing dependency (cross-platform).
    /// </summary>
    public static string Svg(string data, int pixelsPerModule = 6)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(data, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(qrData).GetGraphic(pixelsPerModule);
    }
}
