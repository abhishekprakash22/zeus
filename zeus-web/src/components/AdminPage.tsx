// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import {
  Ban,
  CheckCircle2,
  KeyRound,
  LogOut,
  PackageCheck,
  RefreshCw,
  Save,
  ShieldCheck,
  UserPlus,
  Users,
} from 'lucide-react';
import { useUserAccessStore } from '../state/user-access-store';
import { QrzAccessGate } from './QrzAccessGate';
import {
  fetchInstalledPlugins,
  fetchRegistry,
  type PluginDto,
  type RegistryPluginEntry,
} from '../plugins/api/plugins';
import type {
  ZeusManagedPluginRecord,
  ZeusManagedPluginUpdateRequest,
  ZeusPluginEntitlement,
  ZeusUserRecord,
} from '../api/users';

const SUBSCRIPTION_OPTIONS = [
  'manual',
  'qrz-xml',
  'trial',
  'active',
  'past_due',
  'expired',
  'comped',
  'suspended',
] as const;

const PLUGIN_ACCESS_MODE_OPTIONS = [
  { value: 'all', label: 'all free by default' },
  { value: 'selected', label: 'selected plugins only' },
] as const;

type PluginAccessMode = (typeof PLUGIN_ACCESS_MODE_OPTIONS)[number]['value'];

type PluginRowModel = {
  pluginId: string;
  displayName: string;
  installed: PluginDto | null;
  registry: RegistryPluginEntry | null;
  managed: ZeusManagedPluginRecord | null;
};

function fmtDate(value: string | null | undefined): string {
  if (!value) return 'never';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString();
}

function dateInput(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return date.toISOString().slice(0, 10);
}

function fromDateInput(value: string): string | null {
  return value ? new Date(`${value}T23:59:59.000Z`).toISOString() : null;
}

function normalizePluginId(value: string): string {
  return value.trim().toLowerCase();
}

function centsFromDollars(value: string): number {
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed <= 0) return 0;
  return Math.round(parsed * 100);
}

function dollarsFromCents(value: number): string {
  return (Math.max(0, value) / 100).toFixed(2);
}

function priceLabel(plugin: ZeusManagedPluginRecord | null): string {
  if (!plugin?.subscriptionRequired) return 'free';
  if (plugin.monthlyPriceCents <= 0) return `${plugin.currency} manual`;
  return `${plugin.currency} ${dollarsFromCents(plugin.monthlyPriceCents)}/mo`;
}

function priceLabelForRow(model: PluginRowModel, managed: ZeusManagedPluginRecord | null): string {
  if (managed) return priceLabel(managed);
  const subscription = model.registry?.subscription;
  if (!subscription?.required) return 'free';
  if (subscription.monthlyPriceCents <= 0) return `${subscription.currency} manual`;
  return `${subscription.currency} ${dollarsFromCents(subscription.monthlyPriceCents)}/mo`;
}

function StatusPill({ user }: { user: ZeusUserRecord }) {
  if (!user.accessAllowed) {
    return (
      <span className="admin-pill admin-pill--blocked">
        <Ban size={12} aria-hidden /> blocked
      </span>
    );
  }
  return (
    <span className="admin-pill admin-pill--allowed">
      <CheckCircle2 size={12} aria-hidden /> allowed
    </span>
  );
}

function ManagedPluginRow({
  model,
  saving,
  onSave,
}: {
  model: PluginRowModel;
  saving: boolean;
  onSave: (pluginId: string, request: ZeusManagedPluginUpdateRequest) => Promise<ZeusManagedPluginRecord | null>;
}) {
  const registrySubscription = model.registry?.subscription ?? null;
  const [displayName, setDisplayName] = useState(model.displayName);
  const [subscriptionRequired, setSubscriptionRequired] = useState(
    model.managed?.subscriptionRequired ?? registrySubscription?.required ?? false,
  );
  const [monthlyPrice, setMonthlyPrice] = useState(
    dollarsFromCents(model.managed?.monthlyPriceCents ?? registrySubscription?.monthlyPriceCents ?? 0),
  );
  const [currency, setCurrency] = useState(model.managed?.currency ?? registrySubscription?.currency ?? 'USD');
  const [active, setActive] = useState(model.managed?.active ?? true);
  const [checkoutUrl, setCheckoutUrl] = useState(model.managed?.checkoutUrl ?? registrySubscription?.checkoutUrl ?? '');
  const [notes, setNotes] = useState(model.managed?.notes ?? registrySubscription?.notes ?? '');

  useEffect(() => {
    setDisplayName(model.managed?.displayName || model.installed?.name || model.registry?.name || model.pluginId);
    setSubscriptionRequired(model.managed?.subscriptionRequired ?? registrySubscription?.required ?? false);
    setMonthlyPrice(dollarsFromCents(model.managed?.monthlyPriceCents ?? registrySubscription?.monthlyPriceCents ?? 0));
    setCurrency(model.managed?.currency ?? registrySubscription?.currency ?? 'USD');
    setActive(model.managed?.active ?? true);
    setCheckoutUrl(model.managed?.checkoutUrl ?? registrySubscription?.checkoutUrl ?? '');
    setNotes(model.managed?.notes ?? registrySubscription?.notes ?? '');
  }, [model.installed?.name, model.managed, model.pluginId, model.registry?.name, registrySubscription]);

  async function savePlugin() {
    await onSave(model.pluginId, {
      displayName,
      subscriptionRequired,
      monthlyPriceCents: centsFromDollars(monthlyPrice),
      currency,
      active,
      checkoutUrl,
      notes,
    });
  }

  return (
    <div className="admin-managed-plugin-row">
      <div className="admin-managed-plugin-main">
        <strong>{model.pluginId}</strong>
        <span>
          {model.installed
            ? `installed v${model.installed.version}`
            : model.registry
              ? 'plugin repo'
              : 'catalog entry'}
        </span>
        <em>{priceLabel(model.managed)}</em>
      </div>
      <div className="admin-plugin-edit-grid">
        <label>
          <span>Display name</span>
          <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        </label>
        <label>
          <span>Monthly price</span>
          <input
            type="number"
            min="0"
            step="0.01"
            value={monthlyPrice}
            onChange={(e) => setMonthlyPrice(e.target.value)}
          />
        </label>
        <label>
          <span>Currency</span>
          <input value={currency} onChange={(e) => setCurrency(e.target.value.toUpperCase())} />
        </label>
        <label className="admin-check-field">
          <input
            type="checkbox"
            checked={subscriptionRequired}
            onChange={(e) => setSubscriptionRequired(e.target.checked)}
          />
          <span>Subscription required</span>
        </label>
        <label className="admin-check-field">
          <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} />
          <span>Active in store</span>
        </label>
        <label className="admin-plugin-wide">
          <span>Checkout URL</span>
          <input value={checkoutUrl} onChange={(e) => setCheckoutUrl(e.target.value)} />
        </label>
        <label className="admin-plugin-wide">
          <span>Plugin notes</span>
          <input value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>
      </div>
      <button type="button" className="btn sm active" onClick={() => void savePlugin()} disabled={saving}>
        <Save size={14} aria-hidden />
        SAVE PLUGIN
      </button>
    </div>
  );
}

export function AdminPage() {
  const session = useUserAccessStore((s) => s.session);
  const users = useUserAccessStore((s) => s.users);
  const managedPlugins = useUserAccessStore((s) => s.managedPlugins);
  const checked = useUserAccessStore((s) => s.checked);
  const adminLoading = useUserAccessStore((s) => s.adminLoading);
  const saving = useUserAccessStore((s) => s.saving);
  const adminError = useUserAccessStore((s) => s.adminError);
  const refreshSession = useUserAccessStore((s) => s.refreshSession);
  const loadAdminUsers = useUserAccessStore((s) => s.loadAdminUsers);
  const createUser = useUserAccessStore((s) => s.createUser);
  const updateUser = useUserAccessStore((s) => s.updateUser);
  const updateManagedPlugin = useUserAccessStore((s) => s.updateManagedPlugin);
  const logout = useUserAccessStore((s) => s.logout);

  const [selectedCall, setSelectedCall] = useState<string>('');
  const [newCall, setNewCall] = useState('');
  const [newPluginId, setNewPluginId] = useState('');
  const [newPluginName, setNewPluginName] = useState('');
  const [installedPlugins, setInstalledPlugins] = useState<PluginDto[]>([]);
  const [registryPlugins, setRegistryPlugins] = useState<RegistryPluginEntry[]>([]);
  const [pluginsLoading, setPluginsLoading] = useState(false);
  const [pluginsError, setPluginsError] = useState<string | null>(null);
  const selected = useMemo(
    () => users.find((u) => u.callsign === selectedCall) ?? users[0] ?? null,
    [selectedCall, users],
  );
  const [accessAllowed, setAccessAllowed] = useState(true);
  const [isAdmin, setIsAdmin] = useState(false);
  const [subscriptionStatus, setSubscriptionStatus] = useState('manual');
  const [subscriptionExpires, setSubscriptionExpires] = useState('');
  const [pluginAccessMode, setPluginAccessMode] = useState<PluginAccessMode>('all');
  const [pluginEntitlements, setPluginEntitlements] = useState<ZeusPluginEntitlement[]>([]);
  const [notes, setNotes] = useState('');

  const loadPluginCatalog = useCallback(async () => {
    setPluginsLoading(true);
    setPluginsError(null);
    try {
      const [installed, registry] = await Promise.all([fetchInstalledPlugins(), fetchRegistry()]);
      setInstalledPlugins(installed.plugins.filter((plugin) => !plugin.scanned));
      setRegistryPlugins(registry.catalog.plugins);
    } catch (err) {
      setPluginsError(err instanceof Error ? err.message : String(err));
    } finally {
      setPluginsLoading(false);
    }
  }, []);

  useEffect(() => {
    void refreshSession();
  }, [refreshSession]);

  useEffect(() => {
    if (session?.accessAllowed && session.isAdmin) {
      void loadAdminUsers();
      void loadPluginCatalog();
    }
  }, [loadAdminUsers, loadPluginCatalog, session?.accessAllowed, session?.isAdmin]);

  useEffect(() => {
    const first = users[0];
    if (!selected && first) setSelectedCall(first.callsign);
  }, [selected, users]);

  useEffect(() => {
    if (!selected) return;
    setAccessAllowed(selected.accessAllowed);
    setIsAdmin(selected.isAdmin);
    setSubscriptionStatus(selected.subscriptionStatus || 'manual');
    setSubscriptionExpires(dateInput(selected.subscriptionExpiresUtc));
    setPluginAccessMode(selected.pluginAccessMode || 'all');
    setPluginEntitlements(selected.pluginEntitlements.map((entitlement) => ({ ...entitlement })));
    setNotes(selected.notes ?? '');
  }, [selected]);

  const managedById = useMemo(() => {
    const map = new Map<string, ZeusManagedPluginRecord>();
    managedPlugins.forEach((plugin) => map.set(plugin.pluginId.toLowerCase(), plugin));
    return map;
  }, [managedPlugins]);

  const entitlementById = useMemo(() => {
    const map = new Map<string, ZeusPluginEntitlement>();
    pluginEntitlements.forEach((entitlement) => map.set(entitlement.pluginId.toLowerCase(), entitlement));
    return map;
  }, [pluginEntitlements]);

  const pluginRows = useMemo<PluginRowModel[]>(() => {
    const map = new Map<string, PluginRowModel>();

    managedPlugins.forEach((plugin) => {
      map.set(plugin.pluginId.toLowerCase(), {
        pluginId: plugin.pluginId,
        displayName: plugin.displayName || plugin.pluginId,
        installed: null,
        registry: null,
        managed: plugin,
      });
    });

    registryPlugins.forEach((plugin) => {
      const key = plugin.id.toLowerCase();
      const existing = map.get(key);
      map.set(key, {
        pluginId: plugin.id,
        displayName: existing?.displayName || plugin.name || plugin.id,
        installed: existing?.installed ?? null,
        registry: plugin,
        managed: existing?.managed ?? null,
      });
    });

    installedPlugins.forEach((plugin) => {
      const key = plugin.id.toLowerCase();
      const existing = map.get(key);
      map.set(key, {
        pluginId: plugin.id,
        displayName: existing?.displayName || plugin.name || plugin.id,
        installed: plugin,
        registry: existing?.registry ?? null,
        managed: existing?.managed ?? null,
      });
    });

    selected?.pluginEntitlements.forEach((entitlement) => {
      const key = entitlement.pluginId.toLowerCase();
      if (!map.has(key)) {
        map.set(key, {
          pluginId: entitlement.pluginId,
          displayName: entitlement.pluginId,
          installed: null,
          registry: null,
          managed: null,
        });
      }
    });

    return [...map.values()].sort((a, b) => a.pluginId.localeCompare(b.pluginId));
  }, [installedPlugins, managedPlugins, registryPlugins, selected?.pluginEntitlements]);

  if (!checked || !session?.qrzConnected || !session.accessAllowed) {
    return <QrzAccessGate adminMode />;
  }

  if (!session.isAdmin) {
    return (
      <div className="admin-shell">
        <section className="admin-denied">
          <KeyRound size={24} aria-hidden />
          <h1>Admin Access Required</h1>
          <p>{session.callsign} is signed in through QRZ but is not a Zeus admin.</p>
          <button type="button" className="btn sm" onClick={() => void logout()}>
            <LogOut size={14} aria-hidden /> SIGN OUT
          </button>
        </section>
      </div>
    );
  }

  function upsertEntitlement(pluginId: string, patch: Partial<ZeusPluginEntitlement>) {
    const normalized = normalizePluginId(pluginId);
    if (!normalized) return;

    setPluginEntitlements((current) => {
      const existing = current.find((entitlement) => entitlement.pluginId.toLowerCase() === normalized);
      const next: ZeusPluginEntitlement = {
        pluginId: existing?.pluginId ?? normalized,
        accessAllowed: existing?.accessAllowed ?? true,
        subscriptionStatus: existing?.subscriptionStatus ?? 'active',
        subscriptionExpiresUtc: existing?.subscriptionExpiresUtc ?? null,
        denialReason: existing?.denialReason ?? null,
        ...patch,
      };
      const without = current.filter((entitlement) => entitlement.pluginId.toLowerCase() !== normalized);
      return [...without, next].sort((a, b) => a.pluginId.localeCompare(b.pluginId));
    });
  }

  function removeEntitlement(pluginId: string) {
    const normalized = normalizePluginId(pluginId);
    setPluginEntitlements((current) =>
      current.filter((entitlement) => entitlement.pluginId.toLowerCase() !== normalized),
    );
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const callsign = newCall.trim().toUpperCase();
    if (!callsign) return;
    const user = await createUser({
      callsign,
      accessAllowed: true,
      isAdmin: false,
      subscriptionStatus: 'manual',
      pluginAccessMode: 'all',
      pluginEntitlements: [],
    });
    if (user) {
      setNewCall('');
      setSelectedCall(user.callsign);
    }
  }

  async function onAddPlugin(e: FormEvent) {
    e.preventDefault();
    const pluginId = normalizePluginId(newPluginId);
    if (!pluginId) return;
    const plugin = await updateManagedPlugin(pluginId, {
      displayName: newPluginName.trim() || pluginId,
      subscriptionRequired: false,
      monthlyPriceCents: 0,
      currency: 'USD',
      active: true,
    });
    if (plugin) {
      setNewPluginId('');
      setNewPluginName('');
    }
  }

  async function onSave() {
    if (!selected) return;
    await updateUser(selected.callsign, {
      accessAllowed,
      isAdmin,
      subscriptionStatus,
      subscriptionExpiresUtc: fromDateInput(subscriptionExpires),
      pluginAccessMode,
      pluginEntitlements,
      notes,
    });
  }

  return (
    <div className="admin-shell">
      <header className="admin-topbar">
        <div className="admin-brand">
          <ShieldCheck size={24} strokeWidth={2.2} aria-hidden />
          <div>
            <h1>Zeus User Management</h1>
            <p>Signed in as {session.callsign}</p>
          </div>
        </div>
        <div className="admin-topbar-actions">
          <a className="btn sm" href="/">
            OPEN APP
          </a>
          <button type="button" className="btn sm" onClick={() => void loadAdminUsers()} disabled={adminLoading}>
            <RefreshCw size={14} aria-hidden />
            {adminLoading ? 'REFRESHING' : 'REFRESH'}
          </button>
          <button type="button" className="btn sm" onClick={() => void logout()}>
            <LogOut size={14} aria-hidden />
            SIGN OUT
          </button>
        </div>
      </header>

      <main className="admin-main">
        <section className="admin-summary">
          <div>
            <span>Total users</span>
            <strong>{users.length}</strong>
          </div>
          <div>
            <span>Allowed</span>
            <strong>{users.filter((u) => u.accessAllowed).length}</strong>
          </div>
          <div>
            <span>Admins</span>
            <strong>{users.filter((u) => u.isAdmin).length}</strong>
          </div>
          <div>
            <span>QRZ XML seen</span>
            <strong>{users.filter((u) => u.hasQrzXmlSubscription).length}</strong>
          </div>
          <div>
            <span>Plugin plans</span>
            <strong>{managedPlugins.length}</strong>
          </div>
        </section>

        {adminError && <div className="admin-error">{adminError}</div>}
        {pluginsError && <div className="admin-error">{pluginsError}</div>}

        <section className="admin-grid">
          <div className="admin-users-panel">
            <div className="admin-panel-head">
              <Users size={16} aria-hidden />
              <h2>All Users</h2>
            </div>
            <form className="admin-add-user" onSubmit={onCreate}>
              <input
                value={newCall}
                onChange={(e) => setNewCall(e.target.value.toUpperCase())}
                placeholder="CALLSIGN"
                spellCheck={false}
              />
              <button type="submit" className="btn sm active" disabled={!newCall.trim() || saving}>
                <UserPlus size={14} aria-hidden /> ADD
              </button>
            </form>
            <div className="admin-table-wrap">
              <table className="admin-users-table">
                <thead>
                  <tr>
                    <th>Callsign</th>
                    <th>Access</th>
                    <th>Role</th>
                    <th>Subscription</th>
                    <th>Last login</th>
                  </tr>
                </thead>
                <tbody>
                  {users.map((user) => (
                    <tr
                      key={user.callsign}
                      className={user.callsign === selected?.callsign ? 'is-selected' : ''}
                      onClick={() => setSelectedCall(user.callsign)}
                    >
                      <td>
                        <button type="button" className="admin-call-btn">
                          <strong>{user.callsign}</strong>
                          <span>{user.displayName || user.callsign}</span>
                        </button>
                      </td>
                      <td><StatusPill user={user} /></td>
                      <td>{user.isAdmin ? 'admin' : 'operator'}</td>
                      <td>{user.subscriptionStatus}</td>
                      <td>{fmtDate(user.lastLoginUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          <div className="admin-detail-panel">
            <div className="admin-panel-head">
              <ShieldCheck size={16} aria-hidden />
              <h2>{selected ? selected.callsign : 'Select User'}</h2>
            </div>
            {selected ? (
              <>
                <div className="admin-detail-ledger">
                  <div><span>Display name</span><strong>{selected.displayName || selected.callsign}</strong></div>
                  <div><span>Grid</span><strong>{selected.grid ?? 'none'}</strong></div>
                  <div><span>QRZ XML</span><strong>{selected.hasQrzXmlSubscription ? 'active' : 'basic'}</strong></div>
                  <div><span>Created</span><strong>{fmtDate(selected.createdUtc)}</strong></div>
                  <div><span>Updated</span><strong>{fmtDate(selected.updatedUtc)}</strong></div>
                  <div><span>Expires</span><strong>{fmtDate(selected.subscriptionExpiresUtc)}</strong></div>
                </div>

                <label className="admin-toggle-row">
                  <input
                    type="checkbox"
                    checked={accessAllowed}
                    onChange={(e) => setAccessAllowed(e.target.checked)}
                  />
                  <span>
                    <strong>Allow app access</strong>
                    <small>{accessAllowed ? 'User can enter Zeus after QRZ login' : 'User is stopped at login'}</small>
                  </span>
                </label>

                <label className="admin-toggle-row">
                  <input
                    type="checkbox"
                    checked={isAdmin}
                    onChange={(e) => setIsAdmin(e.target.checked)}
                  />
                  <span>
                    <strong>Admin privileges</strong>
                    <small>Can open /admin and manage users, plugins, and subscriptions</small>
                  </span>
                </label>

                <div className="admin-field-grid">
                  <label>
                    <span>Subscription status</span>
                    <select value={subscriptionStatus} onChange={(e) => setSubscriptionStatus(e.target.value)}>
                      {SUBSCRIPTION_OPTIONS.map((option) => (
                        <option key={option} value={option}>{option}</option>
                      ))}
                    </select>
                  </label>
                  <label>
                    <span>Subscription expires</span>
                    <input
                      type="date"
                      value={subscriptionExpires}
                      onChange={(e) => setSubscriptionExpires(e.target.value)}
                    />
                  </label>
                </div>

                <section className="admin-plugin-access">
                  <div className="admin-section-title">
                    <PackageCheck size={14} aria-hidden />
                    <span>Plugin access</span>
                  </div>
                  <div className="admin-field-grid">
                    <label>
                      <span>Default mode</span>
                      <select
                        value={pluginAccessMode}
                        onChange={(e) => setPluginAccessMode(e.target.value as PluginAccessMode)}
                      >
                        {PLUGIN_ACCESS_MODE_OPTIONS.map((option) => (
                          <option key={option.value} value={option.value}>{option.label}</option>
                        ))}
                      </select>
                    </label>
                    <label>
                      <span>Explicit plugin rows</span>
                      <input value={`${pluginEntitlements.length}`} readOnly />
                    </label>
                  </div>

                  <div className="admin-entitlement-list">
                    {pluginRows.length === 0 ? (
                      <div className="admin-empty-row">No managed plugin records yet.</div>
                    ) : (
                      pluginRows.map((plugin) => {
                        const entitlement = entitlementById.get(plugin.pluginId.toLowerCase()) ?? null;
                        const managed = managedById.get(plugin.pluginId.toLowerCase()) ?? null;
                        const accessValue = entitlement
                          ? entitlement.accessAllowed ? 'grant' : 'block'
                          : 'default';
                        return (
                          <div key={plugin.pluginId} className="admin-entitlement-row">
                            <div className="admin-entitlement-plugin">
                              <strong>{plugin.displayName}</strong>
                              <span>{plugin.pluginId}</span>
                              <em>{priceLabelForRow(plugin, managed)}</em>
                            </div>
                            <label>
                              <span>Access</span>
                              <select
                                value={accessValue}
                                onChange={(e) => {
                                  if (e.target.value === 'default') {
                                    removeEntitlement(plugin.pluginId);
                                  } else if (e.target.value === 'grant') {
                                    upsertEntitlement(plugin.pluginId, {
                                      accessAllowed: true,
                                      subscriptionStatus: entitlement?.subscriptionStatus ?? 'active',
                                      denialReason: null,
                                    });
                                  } else {
                                    upsertEntitlement(plugin.pluginId, {
                                      accessAllowed: false,
                                      subscriptionStatus: entitlement?.subscriptionStatus ?? 'suspended',
                                      denialReason: entitlement?.denialReason ?? 'Plugin subscription required',
                                    });
                                  }
                                }}
                              >
                                <option value="default">default</option>
                                <option value="grant">grant</option>
                                <option value="block">block</option>
                              </select>
                            </label>
                            <label>
                              <span>Status</span>
                              <select
                                value={entitlement?.subscriptionStatus ?? 'manual'}
                                onChange={(e) =>
                                  upsertEntitlement(plugin.pluginId, { subscriptionStatus: e.target.value })}
                              >
                                {SUBSCRIPTION_OPTIONS.map((option) => (
                                  <option key={option} value={option}>{option}</option>
                                ))}
                              </select>
                            </label>
                            <label>
                              <span>Expires</span>
                              <input
                                type="date"
                                value={dateInput(entitlement?.subscriptionExpiresUtc)}
                                onChange={(e) =>
                                  upsertEntitlement(plugin.pluginId, {
                                    subscriptionExpiresUtc: fromDateInput(e.target.value),
                                  })}
                              />
                            </label>
                            <label className="admin-entitlement-reason">
                              <span>Denial reason</span>
                              <input
                                value={entitlement?.denialReason ?? ''}
                                onChange={(e) =>
                                  upsertEntitlement(plugin.pluginId, {
                                    accessAllowed: entitlement?.accessAllowed ?? false,
                                    subscriptionStatus: entitlement?.subscriptionStatus ?? 'suspended',
                                    denialReason: e.target.value,
                                  })}
                              />
                            </label>
                          </div>
                        );
                      })
                    )}
                  </div>
                </section>

                <label className="admin-notes">
                  <span>Admin notes</span>
                  <textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={5} />
                </label>

                <button type="button" className="btn sm active admin-save" onClick={() => void onSave()} disabled={saving}>
                  <Save size={14} aria-hidden />
                  {saving ? 'SAVING' : 'SAVE USER'}
                </button>
              </>
            ) : (
              <div className="admin-empty">No user records yet.</div>
            )}
          </div>
        </section>

        <section className="admin-plugin-panel">
          <div className="admin-panel-head">
            <PackageCheck size={16} aria-hidden />
            <h2>Plugin Management</h2>
            <div className="admin-panel-actions">
              <button type="button" className="btn sm" onClick={() => void loadPluginCatalog()} disabled={pluginsLoading}>
                <RefreshCw size={14} aria-hidden />
                {pluginsLoading ? 'SCANNING' : 'SCAN INSTALLED'}
              </button>
            </div>
          </div>

          <form className="admin-add-plugin" onSubmit={onAddPlugin}>
            <input
              value={newPluginId}
              onChange={(e) => setNewPluginId(e.target.value)}
              placeholder="plugin id, e.g. com.openhpsdr.example"
              spellCheck={false}
            />
            <input
              value={newPluginName}
              onChange={(e) => setNewPluginName(e.target.value)}
              placeholder="display name"
            />
            <button type="submit" className="btn sm active" disabled={!newPluginId.trim() || saving}>
              <PackageCheck size={14} aria-hidden /> ADD PLUGIN
            </button>
          </form>

          <div className="admin-managed-plugin-list">
            {pluginRows.length === 0 ? (
              <div className="admin-empty-row">Add a plugin ID or scan installed plugins to start the catalog.</div>
            ) : (
              pluginRows.map((plugin) => (
                <ManagedPluginRow
                  key={plugin.pluginId}
                  model={plugin}
                  saving={saving}
                  onSave={updateManagedPlugin}
                />
              ))
            )}
          </div>
        </section>
      </main>
    </div>
  );
}
