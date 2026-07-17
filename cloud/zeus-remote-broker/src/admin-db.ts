/**
 * Typed D1 query wrappers for the `zeus-admin` store. Parameterized queries
 * ONLY — never interpolate caller input into SQL. Rows mirror 0001_admin.sql.
 *
 * This is the broker's full admin data layer; the chat relay carries only a
 * tiny read-only helper (it needs just the enabled-admin callsign list).
 */

/** A row in `admins`. pw_* are NULL for password-less (legacy-seeded) admins. */
export interface AdminRow {
  callsign: string;
  pw_hash: string | null;
  pw_salt: string | null;
  pw_iter: number | null;
  disabled: number;
  created_at: number;
  created_by: string | null;
}

/** A row in `admin_tokens`. token_hash is never returned to clients. */
export interface TokenRow {
  id: string;
  token_hash: string;
  callsign: string;
  label: string | null;
  created_at: number;
  expires_at: number | null; // null = never expires (agent/API token); set for session tokens
  last_used_at: number | null;
  revoked: number;
}

/** A managed app user in the remote admin ledger. */
export interface ManagedUserRow {
  callsign: string;
  display_name: string | null;
  access_allowed: number;
  is_admin: number;
  subscription_status: string;
  subscription_expires_at: number | null;
  plugin_access_mode: string;
  plugin_entitlements: string;
  notes: string | null;
  created_at: number;
  updated_at: number;
  last_login_at: number | null;
  stripe_customer_id: string | null;
  stripe_subscription_id: string | null;
  stripe_subscription_status: string | null;
  stripe_current_period_end: number | null;
  credit_balance_cents: number;
}

/** A plugin catalog row with pricing/subscription metadata. */
export interface ManagedPluginRow {
  plugin_id: string;
  display_name: string;
  subscription_required: number;
  monthly_price_cents: number;
  currency: string;
  active: number;
  checkout_url: string | null;
  notes: string | null;
  created_at: number;
  updated_at: number;
  stripe_product_id: string | null;
  stripe_price_id: string | null;
  stripe_price_cents: number | null;
  stripe_price_currency: string | null;
}

// --- admins ----------------------------------------------------------------

export async function getAdmin(db: D1Database, callsign: string): Promise<AdminRow | null> {
  return db.prepare('SELECT * FROM admins WHERE callsign = ?').bind(callsign).first<AdminRow>();
}

export async function listAdmins(db: D1Database): Promise<AdminRow[]> {
  const res = await db
    .prepare('SELECT * FROM admins ORDER BY callsign')
    .all<AdminRow>();
  return res.results ?? [];
}

/** Callsigns of all enabled admins — the chat relay's only need (also reused here). */
export async function listEnabledAdminCallsigns(db: D1Database): Promise<string[]> {
  const res = await db
    .prepare('SELECT callsign FROM admins WHERE disabled = 0')
    .all<{ callsign: string }>();
  return (res.results ?? []).map((r) => r.callsign);
}

export async function countAdmins(db: D1Database): Promise<number> {
  const row = await db.prepare('SELECT COUNT(*) AS n FROM admins').first<{ n: number }>();
  return row?.n ?? 0;
}

/** Enabled (non-disabled) admin count — used to refuse disabling the last admin. */
export async function countEnabledAdmins(db: D1Database): Promise<number> {
  const row = await db
    .prepare('SELECT COUNT(*) AS n FROM admins WHERE disabled = 0')
    .first<{ n: number }>();
  return row?.n ?? 0;
}

/**
 * Insert an admin if absent (idempotent — never demotes/overwrites an existing
 * row). pw_* may all be null for a password-less seed.
 */
export async function insertAdmin(
  db: D1Database,
  row: {
    callsign: string;
    pw_hash: string | null;
    pw_salt: string | null;
    pw_iter: number | null;
    created_by: string;
  },
): Promise<void> {
  await db
    .prepare(
      'INSERT OR IGNORE INTO admins (callsign, pw_hash, pw_salt, pw_iter, disabled, created_at, created_by) ' +
        'VALUES (?, ?, ?, ?, 0, ?, ?)',
    )
    .bind(row.callsign, row.pw_hash, row.pw_salt, row.pw_iter, Date.now(), row.created_by)
    .run();
}

export async function setAdminPassword(
  db: D1Database,
  callsign: string,
  pw_hash: string,
  pw_salt: string,
  pw_iter: number,
): Promise<void> {
  await db
    .prepare('UPDATE admins SET pw_hash = ?, pw_salt = ?, pw_iter = ? WHERE callsign = ?')
    .bind(pw_hash, pw_salt, pw_iter, callsign)
    .run();
}

/** Disable (soft-delete) an admin; never hard-delete, to keep the audit trail. */
export async function disableAdmin(db: D1Database, callsign: string): Promise<void> {
  await db.prepare('UPDATE admins SET disabled = 1 WHERE callsign = ?').bind(callsign).run();
}

// --- tokens ----------------------------------------------------------------

export async function insertToken(
  db: D1Database,
  row: { id: string; token_hash: string; callsign: string; label: string; expires_at: number | null },
): Promise<void> {
  await db
    .prepare(
      'INSERT INTO admin_tokens (id, token_hash, callsign, label, created_at, expires_at, revoked) ' +
        'VALUES (?, ?, ?, ?, ?, ?, 0)',
    )
    .bind(row.id, row.token_hash, row.callsign, row.label, Date.now(), row.expires_at)
    .run();
}

/**
 * Look a token up by its stored SHA-256 hex. Enforces revoked=0 AND not-expired
 * at the DATA layer (pass the current unix-ms), so expiry holds for every
 * consumer rather than depending on an app-level check. A null expires_at never
 * expires (agent tokens).
 */
export async function getTokenByHash(
  db: D1Database,
  tokenHash: string,
  now: number,
): Promise<TokenRow | null> {
  return db
    .prepare(
      'SELECT * FROM admin_tokens WHERE token_hash = ? AND revoked = 0 ' +
        'AND (expires_at IS NULL OR expires_at > ?)',
    )
    .bind(tokenHash, now)
    .first<TokenRow>();
}

export async function touchToken(db: D1Database, id: string): Promise<void> {
  await db
    .prepare('UPDATE admin_tokens SET last_used_at = ? WHERE id = ?')
    .bind(Date.now(), id)
    .run();
}

export async function revokeToken(db: D1Database, id: string): Promise<void> {
  await db.prepare('UPDATE admin_tokens SET revoked = 1 WHERE id = ?').bind(id).run();
}

/** Token metadata for a callsign — never the secret/hash. */
export async function listTokens(
  db: D1Database,
  callsign: string,
): Promise<Array<Omit<TokenRow, 'token_hash'>>> {
  const res = await db
    .prepare(
      'SELECT id, callsign, label, created_at, expires_at, last_used_at, revoked FROM admin_tokens ' +
        'WHERE callsign = ? ORDER BY created_at DESC',
    )
    .bind(callsign)
    .all<Omit<TokenRow, 'token_hash'>>();
  return res.results ?? [];
}

/** The id of the token referenced by a hash (so we can authorize a revoke). */
export async function getTokenOwner(db: D1Database, id: string): Promise<TokenRow | null> {
  return db.prepare('SELECT * FROM admin_tokens WHERE id = ?').bind(id).first<TokenRow>();
}

// --- audit -----------------------------------------------------------------

export async function insertAudit(
  db: D1Database,
  entry: { actor: string | null; action: string; target?: string | null; detail?: string | null },
): Promise<void> {
  await db
    .prepare('INSERT INTO admin_audit (ts, actor, action, target, detail) VALUES (?, ?, ?, ?, ?)')
    .bind(Date.now(), entry.actor, entry.action, entry.target ?? null, entry.detail ?? null)
    .run();
}

// --- managed app users ------------------------------------------------------

export async function listManagedUsers(db: D1Database): Promise<ManagedUserRow[]> {
  const res = await db
    .prepare(
      'SELECT * FROM admin_users ORDER BY is_admin DESC, updated_at DESC, callsign',
    )
    .all<ManagedUserRow>();
  return res.results ?? [];
}

export async function getManagedUser(db: D1Database, callsign: string): Promise<ManagedUserRow | null> {
  return db.prepare('SELECT * FROM admin_users WHERE callsign = ?').bind(callsign).first<ManagedUserRow>();
}

export async function getManagedUserByStripeCustomer(
  db: D1Database,
  stripeCustomerId: string,
): Promise<ManagedUserRow | null> {
  return db.prepare('SELECT * FROM admin_users WHERE stripe_customer_id = ?')
    .bind(stripeCustomerId)
    .first<ManagedUserRow>();
}

/**
 * Record a QRZ-authenticated Zeus operator without changing admin-managed
 * controls. Presence heartbeats use this so every real Zeus user becomes
 * visible in /admin/users while bans, roles, subscriptions, and plugin
 * entitlements remain authoritative.
 */
export async function recordManagedUserLogin(
  db: D1Database,
  callsign: string,
  displayName?: string | null,
): Promise<void> {
  const now = Date.now();
  const safeDisplayName = displayName && displayName.trim() ? displayName.trim() : callsign;
  await db
    .prepare(
      'INSERT INTO admin_users ' +
        '(callsign, display_name, access_allowed, is_admin, subscription_status, subscription_expires_at, plugin_access_mode, plugin_entitlements, notes, created_at, updated_at, last_login_at) ' +
        "VALUES (?, ?, 1, 0, 'manual', NULL, 'all', '[]', NULL, ?, ?, ?) " +
        'ON CONFLICT(callsign) DO UPDATE SET ' +
        "display_name = CASE WHEN admin_users.display_name IS NULL OR admin_users.display_name = '' THEN excluded.display_name ELSE admin_users.display_name END, " +
        'updated_at = excluded.updated_at, last_login_at = excluded.last_login_at',
    )
    .bind(callsign, safeDisplayName, now, now, now)
    .run();
}

export async function upsertManagedUser(
  db: D1Database,
  row: {
    callsign: string;
    display_name?: string | null;
    access_allowed?: number;
    is_admin?: number;
    subscription_status?: string;
    subscription_expires_at?: number | null;
    plugin_access_mode?: string;
    plugin_entitlements?: string;
    notes?: string | null;
    last_login_at?: number | null;
  },
): Promise<ManagedUserRow> {
  const now = Date.now();
  await db
    .prepare(
      'INSERT INTO admin_users ' +
        '(callsign, display_name, access_allowed, is_admin, subscription_status, subscription_expires_at, plugin_access_mode, plugin_entitlements, notes, created_at, updated_at, last_login_at) ' +
        'VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ' +
        'ON CONFLICT(callsign) DO UPDATE SET ' +
        'display_name = excluded.display_name, access_allowed = excluded.access_allowed, is_admin = excluded.is_admin, ' +
        'subscription_status = excluded.subscription_status, subscription_expires_at = excluded.subscription_expires_at, ' +
        'plugin_access_mode = excluded.plugin_access_mode, plugin_entitlements = excluded.plugin_entitlements, ' +
        'notes = excluded.notes, updated_at = excluded.updated_at, last_login_at = COALESCE(excluded.last_login_at, admin_users.last_login_at)',
    )
    .bind(
      row.callsign,
      row.display_name ?? row.callsign,
      row.access_allowed ?? 1,
      row.is_admin ?? 0,
      row.subscription_status ?? 'manual',
      row.subscription_expires_at ?? null,
      row.plugin_access_mode ?? 'all',
      row.plugin_entitlements ?? '[]',
      row.notes ?? null,
      now,
      now,
      row.last_login_at ?? null,
    )
    .run();
  const saved = await getManagedUser(db, row.callsign);
  if (!saved) throw new Error('managed user upsert failed');
  return saved;
}

export async function updateManagedUser(
  db: D1Database,
  callsign: string,
  patch: Partial<Omit<ManagedUserRow, 'callsign' | 'created_at' | 'updated_at'>>,
): Promise<ManagedUserRow | null> {
  const existing = await getManagedUser(db, callsign);
  if (!existing) return null;
  const now = Date.now();
  await db
    .prepare(
      'UPDATE admin_users SET display_name = ?, access_allowed = ?, is_admin = ?, subscription_status = ?, ' +
        'subscription_expires_at = ?, plugin_access_mode = ?, plugin_entitlements = ?, notes = ?, last_login_at = ?, updated_at = ? ' +
        'WHERE callsign = ?',
    )
    .bind(
      patch.display_name ?? existing.display_name,
      patch.access_allowed ?? existing.access_allowed,
      patch.is_admin ?? existing.is_admin,
      patch.subscription_status ?? existing.subscription_status,
      patch.subscription_expires_at !== undefined ? patch.subscription_expires_at : existing.subscription_expires_at,
      patch.plugin_access_mode ?? existing.plugin_access_mode,
      patch.plugin_entitlements ?? existing.plugin_entitlements,
      patch.notes !== undefined ? patch.notes : existing.notes,
      patch.last_login_at !== undefined ? patch.last_login_at : existing.last_login_at,
      now,
      callsign,
    )
    .run();
  return getManagedUser(db, callsign);
}

export async function setManagedUserStripeCustomer(
  db: D1Database,
  callsign: string,
  stripeCustomerId: string,
): Promise<ManagedUserRow | null> {
  await db
    .prepare('UPDATE admin_users SET stripe_customer_id = ?, updated_at = ? WHERE callsign = ?')
    .bind(stripeCustomerId, Date.now(), callsign)
    .run();
  return getManagedUser(db, callsign);
}

export async function setManagedUserStripeSubscription(
  db: D1Database,
  callsign: string,
  row: {
    stripe_subscription_id?: string | null;
    stripe_subscription_status?: string | null;
    stripe_current_period_end?: number | null;
    plugin_entitlements?: string;
  },
): Promise<ManagedUserRow | null> {
  const existing = await getManagedUser(db, callsign);
  if (!existing) return null;
  await db
    .prepare(
      'UPDATE admin_users SET stripe_subscription_id = ?, stripe_subscription_status = ?, ' +
        'stripe_current_period_end = ?, plugin_entitlements = ?, subscription_status = ?, ' +
        'subscription_expires_at = ?, updated_at = ? WHERE callsign = ?',
    )
    .bind(
      row.stripe_subscription_id !== undefined ? row.stripe_subscription_id : existing.stripe_subscription_id,
      row.stripe_subscription_status !== undefined
        ? row.stripe_subscription_status
        : existing.stripe_subscription_status,
      row.stripe_current_period_end !== undefined
        ? row.stripe_current_period_end
        : existing.stripe_current_period_end,
      row.plugin_entitlements ?? existing.plugin_entitlements,
      row.stripe_subscription_status ?? existing.subscription_status,
      row.stripe_current_period_end !== undefined
        ? row.stripe_current_period_end
        : existing.subscription_expires_at,
      Date.now(),
      callsign,
    )
    .run();
  return getManagedUser(db, callsign);
}

export async function addManagedUserCreditCents(
  db: D1Database,
  callsign: string,
  amountCents: number,
): Promise<ManagedUserRow | null> {
  await db
    .prepare('UPDATE admin_users SET credit_balance_cents = credit_balance_cents + ?, updated_at = ? WHERE callsign = ?')
    .bind(Math.max(0, Math.round(amountCents)), Date.now(), callsign)
    .run();
  return getManagedUser(db, callsign);
}

// --- managed plugins --------------------------------------------------------

export async function listManagedPlugins(db: D1Database): Promise<ManagedPluginRow[]> {
  const res = await db
    .prepare('SELECT * FROM managed_plugins ORDER BY plugin_id')
    .all<ManagedPluginRow>();
  return res.results ?? [];
}

export async function getManagedPlugin(db: D1Database, pluginId: string): Promise<ManagedPluginRow | null> {
  return db.prepare('SELECT * FROM managed_plugins WHERE plugin_id = ?').bind(pluginId).first<ManagedPluginRow>();
}

export async function upsertManagedPlugin(
  db: D1Database,
  row: {
    plugin_id: string;
    display_name?: string | null;
    subscription_required?: number;
    monthly_price_cents?: number;
    currency?: string;
    active?: number;
    checkout_url?: string | null;
    notes?: string | null;
  },
): Promise<ManagedPluginRow> {
  const now = Date.now();
  await db
    .prepare(
      'INSERT INTO managed_plugins ' +
        '(plugin_id, display_name, subscription_required, monthly_price_cents, currency, active, checkout_url, notes, created_at, updated_at) ' +
        'VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ' +
        'ON CONFLICT(plugin_id) DO UPDATE SET ' +
        'display_name = excluded.display_name, subscription_required = excluded.subscription_required, ' +
        'monthly_price_cents = excluded.monthly_price_cents, currency = excluded.currency, active = excluded.active, ' +
        'checkout_url = excluded.checkout_url, notes = excluded.notes, updated_at = excluded.updated_at',
    )
    .bind(
      row.plugin_id,
      row.display_name ?? row.plugin_id,
      row.subscription_required ?? 0,
      row.monthly_price_cents ?? 0,
      row.currency ?? 'USD',
      row.active ?? 1,
      row.checkout_url ?? null,
      row.notes ?? null,
      now,
      now,
    )
    .run();
  const saved = await getManagedPlugin(db, row.plugin_id);
  if (!saved) throw new Error('managed plugin upsert failed');
  return saved;
}

export async function setManagedPluginStripeCatalog(
  db: D1Database,
  pluginId: string,
  row: {
    stripe_product_id: string;
    stripe_price_id: string;
    stripe_price_cents: number;
    stripe_price_currency: string;
  },
): Promise<ManagedPluginRow | null> {
  await db
    .prepare(
      'UPDATE managed_plugins SET stripe_product_id = ?, stripe_price_id = ?, ' +
        'stripe_price_cents = ?, stripe_price_currency = ?, updated_at = ? WHERE plugin_id = ?',
    )
    .bind(
      row.stripe_product_id,
      row.stripe_price_id,
      row.stripe_price_cents,
      row.stripe_price_currency,
      Date.now(),
      pluginId,
    )
    .run();
  return getManagedPlugin(db, pluginId);
}
