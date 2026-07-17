// SPDX-License-Identifier: GPL-2.0-or-later
//
// InstalledPlugins — card list of currently loaded plugins, with an
// uninstall control on each card. The "deferred uninstall" 202 case is
// surfaced inline so the operator knows a restart is still required.

import { useEffect, useState } from 'react';

import { usePluginsStore } from '../state/plugins-store';
import type { PluginDto } from '../api/plugins';
import { createPluginCheckout } from '../api/plugins';
import { ConfirmDialog } from '../../layout/ConfirmDialog';
import { pluginAccessFor, useUserAccessStore } from '../../state/user-access-store';

type InstalledListKind = 'plugins' | 'vsts';

const LIST_COPY: Record<
  InstalledListKind,
  {
    testId: string;
    loading: string;
    empty: string;
    loadError: string;
  }
> = {
  plugins: {
    testId: 'plugins-installed',
    loading: 'Loading installed plugins…',
    empty: 'No plugins installed yet. Browse the registry or install from a URL.',
    loadError: 'Couldn’t load plugins',
  },
  vsts: {
    testId: 'plugins-installed-vsts',
    loading: 'Loading installed VSTs…',
    empty: 'No VSTs installed yet. Open the TX or RX Audio Suite and scan a VST3 folder.',
    loadError: 'Couldn’t load VSTs',
  },
};

function PluginCard({ p }: { p: PluginDto }) {
  const uninstall = usePluginsStore((s) => s.uninstall);
  const inflight = usePluginsStore((s) => s.uninstallInflight);
  const [confirmOpen, setConfirmOpen] = useState(false);

  const onUninstall = () => {
    if (inflight) return;
    setConfirmOpen(true);
  };

  return (
    <>
      <div
        style={{
          background: 'var(--bg-2)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-md)',
          padding: 12,
          display: 'flex',
          flexDirection: 'column',
          gap: 8,
        }}
      >
        <div
          style={{
            display: 'flex',
            alignItems: 'baseline',
            justifyContent: 'space-between',
            gap: 12,
          }}
        >
          <div>
            <div style={{ fontWeight: 700, color: 'var(--fg-0)', fontSize: 13 }}>
              {p.name}
            </div>
            <div style={{ color: 'var(--fg-2)', fontSize: 11 }}>
              v{p.version}
            </div>
          </div>
          <button
            type="button"
            className="btn sm"
            onClick={onUninstall}
            disabled={inflight}
            aria-label={`Uninstall ${p.name}`}
          >
            {inflight ? 'WORKING…' : 'UNINSTALL'}
          </button>
        </div>

        {p.description && (
          <div style={{ color: 'var(--fg-1)', lineHeight: 1.5 }}>
            {p.description}
          </div>
        )}

      </div>
      {confirmOpen && (
        <ConfirmDialog
          title="Uninstall plugin"
          confirmLabel="Uninstall"
          onCancel={() => setConfirmOpen(false)}
          onConfirm={() => {
            void uninstall(p.id);
            setConfirmOpen(false);
          }}
        >
          <p>Uninstall {p.name}?</p>
          <p>
            {p.name} will be removed from the host. A restart may be required to
            fully unload the assembly.
          </p>
        </ConfirmDialog>
      )}
    </>
  );
}

export function InstalledPlugins({
  kind = 'plugins',
}: {
  kind?: InstalledListKind;
}) {
  const installed = usePluginsStore((s) => s.installed);
  const installedVsts = usePluginsStore((s) => s.installedVsts);
  const blocked = usePluginsStore((s) => s.blocked);
  const load = usePluginsStore((s) => s.installedLoad);
  const refresh = usePluginsStore((s) => s.refreshInstalled);
  const uninstall = usePluginsStore((s) => s.uninstall);
  const uninstallInflight = usePluginsStore((s) => s.uninstallInflight);
  const uninstallError = usePluginsStore((s) => s.lastUninstallError);
  const uninstallNotice = usePluginsStore((s) => s.lastUninstallNotice);
  const clearUninstall = usePluginsStore((s) => s.clearUninstallFeedback);
  const refreshUserSession = useUserAccessStore((s) => s.refreshSession);
  const [blockedRemove, setBlockedRemove] = useState<PluginDto | null>(null);
  const [checkoutPluginId, setCheckoutPluginId] = useState<string | null>(null);
  const [checkoutError, setCheckoutError] = useState<string | null>(null);

  useEffect(() => {
    if (!load.loaded && !load.inflight) {
      void refresh();
    }
  }, [load.loaded, load.inflight, refresh]);

  const copy = LIST_COPY[kind];
  const items = kind === 'vsts' ? installedVsts : installed;

  async function keepBlockedAccess(plugin: PluginDto) {
    if (checkoutPluginId) return;
    setCheckoutPluginId(plugin.id);
    setCheckoutError(null);
    try {
      const checkout = await createPluginCheckout([plugin.id]);
      if (checkout.url) {
        window.location.href = checkout.url;
        return;
      }
      if (checkout.subscriptionUpdated) {
        await refreshUserSession();
        await refresh();
        return;
      }
      setCheckoutError('Subscription checkout did not return a billing session.');
    } catch (err) {
      setCheckoutError(err instanceof Error ? err.message : String(err));
    } finally {
      setCheckoutPluginId(null);
    }
  }

  return (
    <div
      data-testid={copy.testId}
      style={{ display: 'flex', flexDirection: 'column', gap: 12 }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'baseline',
          justifyContent: 'flex-end',
          gap: 12,
        }}
      >
        <button
          type="button"
          className="btn sm"
          onClick={() => void refresh()}
          disabled={load.inflight}
        >
          {load.inflight ? 'LOADING…' : 'RELOAD'}
        </button>
      </div>

      {uninstallError && (
        <div
          role="alert"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--tx-soft)',
            border: '1px solid var(--tx)',
            color: 'var(--fg-0)',
            display: 'flex',
            justifyContent: 'space-between',
            gap: 8,
          }}
        >
          <span>Uninstall failed: {uninstallError}</span>
          <button
            type="button"
            className="btn sm"
            onClick={clearUninstall}
            aria-label="Dismiss uninstall error"
          >
            ×
          </button>
        </div>
      )}

      {uninstallNotice && (
        <div
          role="status"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--accent-soft)',
            border: '1px solid var(--accent-line)',
            color: 'var(--fg-0)',
            display: 'flex',
            justifyContent: 'space-between',
            gap: 8,
          }}
        >
          <span>{uninstallNotice}</span>
          <button
            type="button"
            className="btn sm"
            onClick={clearUninstall}
            aria-label="Dismiss notice"
          >
            ×
          </button>
        </div>
      )}

      {load.loadError && (
        <div
          role="alert"
          style={{
            padding: 10,
            borderRadius: 'var(--r-sm)',
            background: 'var(--tx-soft)',
            border: '1px solid var(--tx)',
            color: 'var(--fg-0)',
          }}
        >
          {copy.loadError}: {load.loadError}
        </div>
      )}

      {!load.loaded && load.inflight && (
        <div style={{ color: 'var(--fg-2)' }}>{copy.loading}</div>
      )}

      {load.loaded && items.length === 0 && (
        <div
          style={{
            padding: 16,
            background: 'var(--bg-2)',
            border: '1px dashed var(--line-strong)',
            borderRadius: 'var(--r-md)',
            color: 'var(--fg-2)',
            textAlign: 'center',
          }}
        >
          {copy.empty}
        </div>
      )}

      {items.map((p) => (
        <PluginCard key={p.id} p={p} />
      ))}

      {kind === 'plugins' && blocked.length > 0 && (
        <div
          style={{
            padding: 12,
            border: '1px solid var(--accent-line)',
            borderRadius: 'var(--r-md)',
            background: 'var(--accent-soft)',
            color: 'var(--fg-0)',
            display: 'grid',
            gap: 8,
          }}
        >
          <strong style={{ fontSize: 12 }}>Plugin subscriptions required</strong>
          <span style={{ color: 'var(--fg-2)', fontSize: 12 }}>
            These installed plugins are now managed as paid subscriptions. Keep access by subscribing, or remove the plugin.
          </span>
          {checkoutError && (
            <span role="alert" style={{ color: 'var(--tx)', fontSize: 12 }}>
              {checkoutError}
            </span>
          )}
          {blocked.map((p) => {
            const access = pluginAccessFor(p.id);
            const managed = access.managedPlugin;
            const price = managed?.subscriptionRequired && managed.monthlyPriceCents > 0
              ? `${managed.currency} ${(managed.monthlyPriceCents / 100).toFixed(2)}/mo`
              : null;
            return (
              <div
                key={p.id}
                style={{
                  display: 'grid',
                  gridTemplateColumns: 'minmax(0, 1fr) auto auto',
                  gap: 8,
                  alignItems: 'center',
                }}
              >
                <span style={{ display: 'grid', gap: 2, minWidth: 0 }}>
                  <span style={{ fontWeight: 700 }}>{p.name}</span>
                  <span style={{ color: 'var(--fg-2)', fontSize: 12 }}>
                    {price
                      ? `${access.reason ?? 'Plugin subscription required'} - ${price}`
                      : access.reason ?? 'Plugin subscription required'}
                  </span>
                </span>
                <button
                  type="button"
                  className="btn sm active"
                  onClick={() => void keepBlockedAccess(p)}
                  disabled={checkoutPluginId !== null || uninstallInflight}
                >
                  {checkoutPluginId === p.id ? 'WORKING…' : 'KEEP ACCESS'}
                </button>
                <button
                  type="button"
                  className="btn sm"
                  onClick={() => setBlockedRemove(p)}
                  disabled={checkoutPluginId !== null || uninstallInflight}
                >
                  REMOVE
                </button>
              </div>
            );
          })}
        </div>
      )}

      {blockedRemove && (
        <ConfirmDialog
          title="Remove paid plugin"
          confirmLabel="Remove"
          onCancel={() => setBlockedRemove(null)}
          onConfirm={() => {
            void uninstall(blockedRemove.id);
            setBlockedRemove(null);
          }}
        >
          <p>Remove {blockedRemove.name}?</p>
          <p>
            Access is currently blocked by subscription policy. Removing it keeps Zeus compliant with the admin plugin policy.
          </p>
        </ConfirmDialog>
      )}
    </div>
  );
}

export function InstalledVsts() {
  return <InstalledPlugins kind="vsts" />;
}
