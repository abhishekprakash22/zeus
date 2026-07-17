// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
using Zeus.Plugins.Host;

namespace Zeus.Server.Hosting;

public sealed class PluginInstallAccessGate : IPluginInstallAccessGate
{
    private readonly QrzService _qrz;
    private readonly UserManagementStore _users;
    private readonly RemoteUserAccessClient _remote;
    private readonly ILogger<PluginInstallAccessGate> _log;

    public PluginInstallAccessGate(
        QrzService qrz,
        UserManagementStore users,
        RemoteUserAccessClient remote,
        ILogger<PluginInstallAccessGate> log)
    {
        _qrz = qrz;
        _users = users;
        _remote = remote;
        _log = log;
    }

    public async Task<PluginInstallAccessDecision> CheckInstallAsync(string pluginId, CancellationToken ct)
    {
        var normalized = pluginId.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return PluginInstallAccessDecision.Deny("plugin id required");

        var qrzStatus = _qrz.GetStatus();
        // STANDALONE: installing a plugin into one's own local Zeus does not
        // require an account. Upstream denies outright when no QRZ session is
        // present, which would leave the plugin catalog permanently empty on an
        // account-less build. Entitlement checks below still apply when a session
        // does exist, so a real subscription policy is unaffected.
        if (!qrzStatus.Connected && ZeusStandalone.Enabled)
            return AccessFor(_users.GetSession(qrzStatus), normalized);

        if (!qrzStatus.Connected)
            return PluginInstallAccessDecision.Deny("QRZ login required");

        if (!_remote.Enabled)
            return AccessFor(_users.GetSession(qrzStatus), normalized);

        var remote = await _remote.GetSessionResultAsync(_qrz, qrzStatus, ct).ConfigureAwait(false);
        if (remote.Outcome == RemoteUserAccessSessionOutcome.Unavailable)
        {
            _log.LogWarning("plugin install remote user management unavailable; falling back to local QRZ user policy");
            return AccessFor(_users.GetSession(qrzStatus), normalized);
        }

        return AccessFor(remote.Session!, normalized);
    }

    private static PluginInstallAccessDecision AccessFor(ZeusUserSession session, string pluginId)
    {
        if (!session.AccessAllowed)
            return PluginInstallAccessDecision.Deny(session.DenialReason ?? "Zeus app access disabled");

        var entitlement = session.PluginEntitlements.FirstOrDefault(e =>
            string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        var managedPlugin = session.ManagedPlugins.FirstOrDefault(p =>
            string.Equals(p.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

        if (entitlement is not null)
        {
            if (!entitlement.AccessAllowed)
                return PluginInstallAccessDecision.Deny(entitlement.DenialReason ?? "Plugin subscription required");
            if (entitlement.SubscriptionExpiresUtc is { } expiry && expiry <= DateTime.UtcNow)
                return PluginInstallAccessDecision.Deny("Plugin subscription expired");
            return PluginInstallAccessDecision.Allow;
        }

        if (managedPlugin is { Active: false })
            return PluginInstallAccessDecision.Deny("Plugin disabled by Zeus admin");

        if (managedPlugin?.SubscriptionRequired == true || session.PluginAccessMode == "selected")
            return PluginInstallAccessDecision.Deny("Plugin subscription required");

        return PluginInstallAccessDecision.Allow;
    }
}
