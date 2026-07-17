/**
 * /admin/* API: credential-based admin auth backed by the `zeus-admin` D1 store.
 *
 * Auth model (ADMIN_AUTH_DESIGN.md): QRZ proves callsign *ownership*; the admin
 * store proves *authorization*. Interactive callers POST /admin/login with a
 * password (plus QRZ headers) to mint a short-lived `session` token; agents hold
 * a non-expiring `agent` token. Protected routes require `Authorization: Bearer
 * zsa_…`. Failures return generic 401s — no oracle on which factor failed.
 */

import type { Env } from './types';
import {
  hashPassword,
  verifyPassword,
  mintToken,
  hashToken,
  looksLikeToken,
  SESSION_TTL_MS,
  type PasswordHash,
} from './admin-auth';
import {
  getAdmin,
  listAdmins,
  insertAdmin,
  setAdminPassword,
  disableAdmin,
  insertToken,
  getTokenByHash,
  touchToken,
  revokeToken,
  listTokens,
  getTokenOwner,
  insertAudit,
  countAdmins,
  countEnabledAdmins,
  listManagedUsers,
  recordManagedUserLogin,
  upsertManagedUser,
  updateManagedUser,
  addManagedUserCreditCents,
  listManagedPlugins,
  setManagedPluginStripeCatalog,
  upsertManagedPlugin,
  type ManagedUserRow,
  type ManagedPluginRow,
} from './admin-db';
import {
  ensureStripeCatalogForPlugin,
  grantStripeCustomerCredit,
  stripeConfigured,
} from './stripe-billing';

/** Resolved identity of an authenticated caller. */
export interface AuthCtx {
  callsign: string;
  tokenId: string;
  label: string;
}

/** Module-scoped guard so the (idempotent) bootstrap+migration runs at most once per isolate. */
let bootstrapped = false;
const DEFAULT_PLUGIN_REGISTRY_URL =
  'https://raw.githubusercontent.com/OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins/main/registry.json';
const PLUGIN_REGISTRY_CACHE_MS = 5 * 60 * 1000;
const USER_DIRECTORY_ONLINE_MS = 90 * 1000;
let pluginRegistryCache: { url: string; fetchedAt: number; plugins: RegistryPluginSeed[] } | null = null;

interface RegistryPluginSeed {
  pluginId: string;
  displayName: string;
  subscriptionRequired: boolean;
  monthlyPriceCents: number;
  currency: string;
  checkoutUrl: string | null;
  notes: string | null;
}

export interface ManagedPluginDto {
  pluginId: string;
  displayName: string;
  subscriptionRequired: boolean;
  monthlyPriceCents: number;
  currency: string;
  active: boolean;
  checkoutUrl: string | null;
  notes: string | null;
  createdAt: number | null;
  updatedAt: number | null;
  source?: 'registry' | 'admin';
  stripeProductId?: string | null;
  stripePriceId?: string | null;
  stripeSyncEnabled?: boolean;
}

interface PresenceOperatorDto {
  callsign: string;
  since: number | null;
  lastSeen: number | null;
  ip: string | null;
  country: string | null;
  platform: string | null;
  appVersion: string | null;
  radioBoard: string | null;
  radioModel: string | null;
  radioConnected: boolean;
}

export async function handleAdmin(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
): Promise<Response> {
  const cors = corsHeaders(env, request);
  if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: cors });

  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, ''); // tolerate trailing slash

  // Seed bootstrap admin + migrate legacy env.ADMINS lazily on the first /admin
  // call (guarded so it never touches the signaling hot-path).
  await ensureBootstrap(env);

  try {
    // --- public: interactive login (mints a session token) -------------------
    if (path === '/admin/login' && request.method === 'POST') {
      return await handleLogin(request, env, cors);
    }

    // --- everything else is Bearer-token protected ---------------------------
    const auth = await authenticate(request, env);
    if (!auth) return json({ error: 'unauthorized' }, 401, cors);

    // tokens
    if (path === '/admin/tokens' && request.method === 'POST') {
      return await handleMintToken(request, env, auth, cors);
    }
    if (path === '/admin/tokens' && request.method === 'GET') {
      return json({ tokens: await listTokens(env.ADMIN_DB, auth.callsign) }, 200, cors);
    }
    const tokDel = /^\/admin\/tokens\/([^/]+)$/.exec(path);
    if (tokDel && request.method === 'DELETE') {
      return await handleRevokeToken(decodeURIComponent(tokDel[1]), env, auth, cors);
    }

    // admins
    if (path === '/admin/admins' && request.method === 'GET') {
      const admins = (await listAdmins(env.ADMIN_DB)).map((a) => ({
        callsign: a.callsign,
        disabled: a.disabled === 1,
        hasPassword: a.pw_hash != null,
        created_at: a.created_at,
        created_by: a.created_by,
      }));
      return json({ admins }, 200, cors);
    }
    if (path === '/admin/admins' && request.method === 'POST') {
      return await handleAddAdmin(request, env, auth, cors);
    }
    const setPw = /^\/admin\/admins\/([^/]+)\/password$/.exec(path);
    if (setPw && request.method === 'POST') {
      return await handleSetPassword(decodeURIComponent(setPw[1]), request, env, auth, cors);
    }
    const adminDel = /^\/admin\/admins\/([^/]+)$/.exec(path);
    if (adminDel && request.method === 'DELETE') {
      return await handleDisableAdmin(decodeURIComponent(adminDel[1]), env, auth, cors);
    }

    // app users / subscriptions
    if (path === '/admin/users' && request.method === 'GET') {
      const presence = await listPresenceOperators(env);
      await Promise.all(presence.map((op) => recordManagedUserLogin(env.ADMIN_DB, op.callsign)));
      const presenceByCall = new Map(presence.map((op) => [op.callsign, op]));
      const now = Date.now();
      const [users, plugins] = await Promise.all([
        listManagedUsers(env.ADMIN_DB),
        mergedManagedPlugins(env),
      ]);
      return json({
        users: users.map((user) => toManagedUserDto(user, presenceByCall.get(user.callsign) ?? null, now)),
        plugins,
        pluginRegistryUrl: pluginRegistryUrl(env),
      }, 200, cors);
    }
    if (path === '/admin/users' && request.method === 'POST') {
      return await handleUpsertManagedUser(request, env, auth, cors);
    }
    const userPut = /^\/admin\/users\/([^/]+)$/.exec(path);
    if (userPut && request.method === 'PUT') {
      return await handleUpdateManagedUser(decodeURIComponent(userPut[1]), request, env, auth, cors);
    }
    const userCredit = /^\/admin\/users\/([^/]+)\/credits$/.exec(path);
    if (userCredit && request.method === 'POST') {
      return await handleGrantUserCredit(decodeURIComponent(userCredit[1]), request, env, auth, cors);
    }

    // plugin catalog / pricing
    if (path === '/admin/plugins' && request.method === 'GET') {
      return json({
        plugins: await mergedManagedPlugins(env),
        pluginRegistryUrl: pluginRegistryUrl(env),
      }, 200, cors);
    }
    if (path === '/admin/plugins' && request.method === 'POST') {
      return await handleUpsertManagedPlugin(null, request, env, auth, cors);
    }
    const pluginPut = /^\/admin\/plugins\/([^/]+)$/.exec(path);
    if (pluginPut && request.method === 'PUT') {
      return await handleUpsertManagedPlugin(decodeURIComponent(pluginPut[1]), request, env, auth, cors);
    }

    // presence
    if (path === '/admin/presence' && request.method === 'GET') {
      return json({ operators: await listPresenceOperators(env) }, 200, cors);
    }

    // diagnostics-session request (remote-diag P3): notify the target operator and
    // hand the dashboard a single-use ticket to open the support WebSocket with.
    if (path === '/admin/request' && request.method === 'POST') {
      return await handleSupportRequest(request, env, auth, cors);
    }

    return json({ error: 'not found' }, 404, cors);
  } catch {
    // Never leak internals; never reveal which step failed.
    return json({ error: 'error' }, 500, cors);
  }
}

// --- login -----------------------------------------------------------------

async function handleLogin(
  request: Request,
  env: Env,
  cors: Record<string, string>,
): Promise<Response> {
  const body = await safeJson(request);
  const password = typeof body.password === 'string' ? body.password : '';
  // Callsign comes from the body; the legacy X-QRZ-Callsign header is still
  // accepted as a fallback so older clients keep working.
  const callsign = norm(
    (typeof body.callsign === 'string' ? body.callsign : '') ||
      (request.headers.get('X-QRZ-Callsign') ?? ''),
  );

  // Generic failure for ALL of: missing inputs, unknown/disabled admin,
  // wrong/absent password. No oracle on which factor failed.
  const fail = () => json({ error: 'unauthorized' }, 401, cors);

  if (!password || !callsign) return fail();

  // Per-callsign throttle on top of the per-IP gate in index.ts, so a botnet
  // spread across many IPs still can't brute-force one admin's password (each
  // PBKDF2 verify is also real server CPU). Soft limit (429), not a hard lock.
  if (await loginRateLimited(env, callsign)) {
    return json({ error: 'rate limited' }, 429, cors);
  }

  // Admin store: callsign is an enabled admin with a password set, and it
  // matches. The admin password is the sole secret — callsigns are public, so
  // the strong password + rate limiting are what gate access here.
  const admin = await getAdmin(env.ADMIN_DB, callsign);
  if (!admin || admin.disabled === 1 || !admin.pw_hash || !admin.pw_salt || admin.pw_iter == null) {
    return fail();
  }
  const stored: PasswordHash = { hash: admin.pw_hash, salt: admin.pw_salt, iter: admin.pw_iter };
  if (!(await verifyPassword(password, stored))) return fail();

  // Success: mint a short-lived session token. Expiry is persisted (expires_at)
  // so the data layer — not one app-level if — enforces it for every consumer.
  const minted = await mintToken();
  const id = crypto.randomUUID();
  const expiresAt = Date.now() + SESSION_TTL_MS;
  await insertToken(env.ADMIN_DB, {
    id,
    token_hash: minted.hash,
    callsign,
    label: 'session',
    expires_at: expiresAt,
  });
  await insertAudit(env.ADMIN_DB, { actor: callsign, action: 'login', target: callsign });
  // Secret in the body — never let an intermediary cache it.
  return json({ token: minted.token, callsign, expiresAt }, 200, cors, { 'Cache-Control': 'no-store' });
}

// --- token endpoints -------------------------------------------------------

async function handleMintToken(
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const body = await safeJson(request);
  const label = (typeof body.label === 'string' && body.label.trim() ? body.label.trim() : 'agent').slice(0, 64);
  const minted = await mintToken();
  const id = crypto.randomUUID();
  // Agent/API tokens do not expire (expires_at null); they are revocable instead.
  await insertToken(env.ADMIN_DB, { id, token_hash: minted.hash, callsign: auth.callsign, label, expires_at: null });
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'token.mint', target: id, detail: label });
  // token shown once — never cache the secret-bearing body.
  return json({ id, token: minted.token }, 201, cors, { 'Cache-Control': 'no-store' });
}

async function handleRevokeToken(
  id: string,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const owner = await getTokenOwner(env.ADMIN_DB, id);
  // Any admin may revoke any token; but if the id is unknown, respond the same
  // way (idempotent revoke) rather than 404 — no enumeration oracle.
  await revokeToken(env.ADMIN_DB, id);
  await insertAudit(env.ADMIN_DB, {
    actor: auth.callsign,
    action: 'token.revoke',
    target: id,
    detail: owner?.callsign ?? null,
  });
  return json({ ok: true }, 200, cors);
}

// --- admin endpoints -------------------------------------------------------

async function handleAddAdmin(
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const body = await safeJson(request);
  const callsign = norm((body.callsign as string) ?? '');
  if (!callsign) return json({ error: 'callsign required' }, 400, cors);

  let pw: PasswordHash | null = null;
  if (typeof body.password === 'string' && body.password) pw = await hashPassword(body.password);

  await insertAdmin(env.ADMIN_DB, {
    callsign,
    pw_hash: pw?.hash ?? null,
    pw_salt: pw?.salt ?? null,
    pw_iter: pw?.iter ?? null,
    created_by: auth.callsign,
  });
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'admin.add', target: callsign });
  return json({ ok: true, callsign }, 201, cors);
}

async function handleSetPassword(
  target: string,
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const callsign = norm(target);
  const body = await safeJson(request);
  const password = typeof body.password === 'string' ? body.password : '';
  if (!password) return json({ error: 'password required' }, 400, cors);

  const admin = await getAdmin(env.ADMIN_DB, callsign);
  if (!admin) return json({ error: 'not found' }, 404, cors);

  // An admin may set a password on themselves, or ONBOARD a password-less admin
  // (e.g. a legacy-seeded callsign), but may NOT reset a peer's EXISTING
  // password — that would be silent account takeover between equals.
  if (callsign !== auth.callsign && admin.pw_hash) {
    return json({ error: 'forbidden' }, 403, cors);
  }

  const pw = await hashPassword(password);
  await setAdminPassword(env.ADMIN_DB, callsign, pw.hash, pw.salt, pw.iter);
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'admin.setpw', target: callsign });
  return json({ ok: true }, 200, cors);
}

async function handleDisableAdmin(
  target: string,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const callsign = norm(target);

  // Never let the system be locked out: refuse to disable the last enabled
  // admin. (This also stops a single rogue/compromised admin from disabling
  // everyone else AND themselves into an unrecoverable state.)
  const admin = await getAdmin(env.ADMIN_DB, callsign);
  if (admin && admin.disabled === 0 && (await countEnabledAdmins(env.ADMIN_DB)) <= 1) {
    return json({ error: 'cannot disable the last enabled admin' }, 409, cors);
  }

  await disableAdmin(env.ADMIN_DB, callsign);
  await insertAudit(env.ADMIN_DB, {
    actor: auth.callsign,
    action: 'admin.disable',
    target: callsign,
    detail: admin ? 'existed' : 'absent',
  });
  return json({ ok: true }, 200, cors);
}

// --- managed app users ------------------------------------------------------

async function handleUpsertManagedUser(
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const body = await safeJson(request);
  const callsign = norm((body.callsign as string) ?? '');
  if (!callsign) return json({ error: 'callsign required' }, 400, cors);

  const user = await upsertManagedUser(env.ADMIN_DB, normalizedUserRow(callsign, body));
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'user.upsert', target: callsign });
  return json(toManagedUserDto(user), 200, cors);
}

async function handleUpdateManagedUser(
  target: string,
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const callsign = norm(target);
  if (!callsign) return json({ error: 'callsign required' }, 400, cors);
  const body = await safeJson(request);
  const user = await updateManagedUser(env.ADMIN_DB, callsign, normalizedUserPatch(body));
  if (!user) return json({ error: 'not found' }, 404, cors);
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'user.update', target: callsign });
  return json(toManagedUserDto(user), 200, cors);
}

async function handleGrantUserCredit(
  target: string,
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const callsign = norm(target);
  if (!callsign) return json({ error: 'callsign required' }, 400, cors);
  const body = await safeJson(request);
  const amountCents = Math.max(0, Math.round(optionalNumber(body.amountCents) ?? 0));
  if (amountCents <= 0) return json({ error: 'amountCents required' }, 400, cors);

  const existing = (await updateManagedUser(env.ADMIN_DB, callsign, {}))
    ?? (await upsertManagedUser(env.ADMIN_DB, normalizedUserRow(callsign, { callsign })));
  if (existing.stripe_customer_id) {
    await grantStripeCustomerCredit(
      env,
      existing.stripe_customer_id,
      amountCents,
      optionalString(body.currency) ?? 'USD',
      optionalString(body.note),
    );
  }
  const user = await addManagedUserCreditCents(env.ADMIN_DB, callsign, amountCents);
  await insertAudit(env.ADMIN_DB, {
    actor: auth.callsign,
    action: 'user.credit',
    target: callsign,
    detail: String(amountCents),
  });
  return json(toManagedUserDto(user ?? existing), 200, cors);
}

// --- managed plugins --------------------------------------------------------

async function handleUpsertManagedPlugin(
  targetPluginId: string | null,
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const body = await safeJson(request);
  const pluginId = normPluginId(targetPluginId ?? ((body.pluginId as string) || ''));
  if (!pluginId) return json({ error: 'plugin id required' }, 400, cors);

  const plugin = await upsertManagedPlugin(env.ADMIN_DB, normalizedPluginRow(pluginId, body));
  const synced = await maybeSyncStripeCatalog(env, plugin);
  const responsePlugin = synced ?? plugin;
  await insertAudit(env.ADMIN_DB, {
    actor: auth.callsign,
    action: 'plugin.upsert',
    target: pluginId,
    detail: String(plugin.monthly_price_cents),
  });
  return json(toManagedPluginDto(responsePlugin), 200, cors);
}

async function maybeSyncStripeCatalog(env: Env, plugin: ManagedPluginRow): Promise<ManagedPluginRow | null> {
  const sync = await ensureStripeCatalogForPlugin(env, plugin);
  if (!sync) return plugin;
  return setManagedPluginStripeCatalog(env.ADMIN_DB, plugin.plugin_id, {
    stripe_product_id: sync.productId,
    stripe_price_id: sync.priceId,
    stripe_price_cents: sync.priceCents,
    stripe_price_currency: sync.priceCurrency,
  });
}

// --- support request --------------------------------------------------------

/**
 * Ask the target operator's SignalRoom to mint a maintainer-support request and
 * notify the radio (host). On success the dashboard gets a single-use `ticket` it
 * opens `role=support&callsign=<target>&ticket=<ticket>` with; the operator must
 * still approve at the radio before anything flows (remote-diag P3). 503 when the
 * operator is offline (no host socket).
 */
async function handleSupportRequest(
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const target = norm(((await safeJson(request)).callsign as string) ?? '');
  if (!target) return json({ error: 'callsign required' }, 400, cors);

  const room = env.SIGNAL_ROOM.get(env.SIGNAL_ROOM.idFromName(target));
  const res = await room.fetch('https://signal.internal/support-request', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ admin: auth.callsign }),
  });

  if (res.status === 503) {
    await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'request', target, detail: 'offline' });
    return json({ error: 'operator offline' }, 503, cors);
  }
  if (!res.ok) return json({ error: 'error' }, 502, cors);

  const { requestId, ticket } = await res.json<{ requestId: string; ticket: string }>();
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'request', target, detail: requestId });
  // The ticket is a short-lived secret — never let an intermediary cache it.
  return json({ ok: true, requestId, ticket, callsign: target }, 200, cors, { 'Cache-Control': 'no-store' });
}

// --- auth -------------------------------------------------------------------

/**
 * Resolve a `Authorization: Bearer zsa_…` header to an admin identity, or null.
 * Rejects (as null) on: missing/malformed token, unknown/revoked token, expired
 * session token, or disabled admin. Bumps last_used_at on success.
 *
 * Exported as {@link verifyAdminToken} so sibling routes (e.g. /admin/crashes,
 * which lives outside this module's path-dispatch) can gate on the SAME
 * credential-based admin auth without re-implementing the token-hash/expiry/
 * disabled-admin checks. Callers that do their own bootstrap-independent routing
 * should call {@link ensureAdminBootstrap} once first.
 */
export { authenticate as verifyAdminToken, ensureBootstrap as ensureAdminBootstrap };

async function authenticate(request: Request, env: Env): Promise<AuthCtx | null> {
  const header = request.headers.get('Authorization') ?? '';
  const m = /^Bearer\s+(.+)$/i.exec(header.trim());
  if (!m) return null;
  const token = m[1].trim();
  if (!looksLikeToken(token)) return null;

  const tokenHash = await hashToken(token);
  // getTokenByHash enforces revoked=0 AND not-expired at the data layer, so
  // expiry holds for every consumer of the shared store, not just this call.
  const row = await getTokenByHash(env.ADMIN_DB, tokenHash, Date.now());
  if (!row) return null;

  // The owning admin must still be enabled.
  const admin = await getAdmin(env.ADMIN_DB, row.callsign);
  if (!admin || admin.disabled === 1) return null;

  await touchToken(env.ADMIN_DB, row.id);
  return { callsign: row.callsign, tokenId: row.id, label: row.label ?? '' };
}

// --- bootstrap & migration --------------------------------------------------

/**
 * Idempotent: when `admins` is empty, seed the bootstrap admin (if the secrets
 * are set) and migrate legacy env.ADMINS callsigns to password-less rows. Guarded
 * by a module flag AND a count check so it costs at most one COUNT(*) per isolate
 * after the first call.
 */
async function ensureBootstrap(env: Env): Promise<void> {
  if (bootstrapped) return;
  bootstrapped = true;
  try {
    if ((await countAdmins(env.ADMIN_DB)) > 0) return; // already provisioned

    if (env.ADMIN_BOOTSTRAP_CALLSIGN && env.ADMIN_BOOTSTRAP_PASSWORD) {
      const callsign = norm(env.ADMIN_BOOTSTRAP_CALLSIGN);
      const pw = await hashPassword(env.ADMIN_BOOTSTRAP_PASSWORD);
      await insertAdmin(env.ADMIN_DB, {
        callsign,
        pw_hash: pw.hash,
        pw_salt: pw.salt,
        pw_iter: pw.iter,
        created_by: 'bootstrap',
      });
      await insertAudit(env.ADMIN_DB, { actor: 'bootstrap', action: 'admin.add', target: callsign });
    }

    // Seed legacy ADMINS list as password-less rows (never demote/remove).
    for (const callsign of (env.ADMINS ?? '').split(',').map(norm).filter(Boolean)) {
      await insertAdmin(env.ADMIN_DB, {
        callsign,
        pw_hash: null,
        pw_salt: null,
        pw_iter: null,
        created_by: 'bootstrap',
      });
    }
  } catch {
    // If bootstrap fails (e.g. schema not yet applied), allow a later retry.
    bootstrapped = false;
  }
}

// --- helpers ----------------------------------------------------------------

export async function mergedManagedPlugins(env: Env) {
  const [rows, registry] = await Promise.all([
    listManagedPlugins(env.ADMIN_DB),
    fetchRegistryPluginSeeds(env),
  ]);
  const byId = new Map<string, ManagedPluginDto>();
  for (const seed of registry) {
    byId.set(seed.pluginId, {
      pluginId: seed.pluginId,
      displayName: seed.displayName,
      subscriptionRequired: seed.subscriptionRequired,
      monthlyPriceCents: seed.monthlyPriceCents,
      currency: seed.currency,
      active: true,
      checkoutUrl: null,
      notes: seed.notes,
      createdAt: null,
      updatedAt: null,
      source: 'registry',
      stripeProductId: null,
      stripePriceId: null,
      stripeSyncEnabled: stripeConfigured(env),
    });
  }
  for (const row of rows) {
    byId.set(row.plugin_id, { ...toManagedPluginDto(row), source: 'admin' });
  }
  return [...byId.values()].sort((a, b) => a.pluginId.localeCompare(b.pluginId));
}

async function fetchRegistryPluginSeeds(env: Env): Promise<RegistryPluginSeed[]> {
  const url = pluginRegistryUrl(env);
  const now = Date.now();
  if (pluginRegistryCache?.url === url && now - pluginRegistryCache.fetchedAt < PLUGIN_REGISTRY_CACHE_MS) {
    return pluginRegistryCache.plugins;
  }

  try {
    const res = await fetch(url, { headers: { accept: 'application/json' } });
    if (!res.ok) return [];
    const raw = (await res.json()) as Record<string, unknown>;
    const plugins = Array.isArray(raw.plugins)
      ? raw.plugins.map(registryPluginSeed).filter((p): p is RegistryPluginSeed => p !== null)
      : [];
    pluginRegistryCache = { url, fetchedAt: now, plugins };
    return plugins;
  } catch {
    return [];
  }
}

function registryPluginSeed(raw: unknown): RegistryPluginSeed | null {
  if (!raw || typeof raw !== 'object') return null;
  const plugin = raw as Record<string, unknown>;
  const pluginId = normPluginId(String(plugin.id ?? ''));
  if (!pluginId) return null;
  const subscription = plugin.subscription && typeof plugin.subscription === 'object'
    ? (plugin.subscription as Record<string, unknown>)
    : {};
  const cents = optionalNumber(subscription.monthlyPriceCents) ?? 0;
  return {
    pluginId,
    displayName: optionalString(plugin.name) ?? pluginId,
    subscriptionRequired: subscription.required === true,
    monthlyPriceCents: Math.max(0, Math.round(cents)),
    currency: (optionalString(subscription.currency) ?? 'USD').toUpperCase(),
    checkoutUrl: optionalString(subscription.checkoutUrl),
    notes: optionalString(subscription.notes),
  };
}

function pluginRegistryUrl(env: Env): string {
  return optionalString(env.PLUGIN_REGISTRY_URL) ?? DEFAULT_PLUGIN_REGISTRY_URL;
}

async function listPresenceOperators(env: Env): Promise<PresenceOperatorDto[]> {
  const id = env.PRESENCE.idFromName('global');
  const res = await env.PRESENCE.get(id).fetch('https://presence.internal/list');
  if (!res.ok) return [];
  const body = await res.json<{ operators?: unknown[] }>().catch(() => ({ operators: [] }));
  const operators = Array.isArray(body.operators) ? body.operators : [];
  return operators.map(normalizedPresenceOperator).filter((op): op is PresenceOperatorDto => op !== null);
}

function normalizedPresenceOperator(raw: unknown): PresenceOperatorDto | null {
  if (!raw || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  const callsign = norm(String(o.callsign ?? ''));
  if (!callsign) return null;
  return {
    callsign,
    since: optionalNumber(o.since),
    lastSeen: optionalNumber(o.lastSeen),
    ip: optionalString(o.ip),
    country: optionalString(o.country),
    platform: optionalString(o.platform),
    appVersion: optionalString(o.appVersion),
    radioBoard: optionalString(o.radioBoard),
    radioModel: optionalString(o.radioModel),
    radioConnected: o.radioConnected === true,
  };
}

function toManagedUserDto(
  row: ManagedUserRow,
  presence: PresenceOperatorDto | null = null,
  now: number = Date.now(),
) {
  const directoryOnline = row.last_login_at !== null && now - row.last_login_at <= USER_DIRECTORY_ONLINE_MS;
  const online = presence !== null || directoryOnline;
  return {
    callsign: row.callsign,
    displayName: row.display_name ?? row.callsign,
    accessAllowed: row.access_allowed === 1,
    isAdmin: row.is_admin === 1,
    subscriptionStatus: row.subscription_status || 'manual',
    subscriptionExpiresAt: row.subscription_expires_at,
    pluginAccessMode: row.plugin_access_mode === 'selected' ? 'selected' : 'all',
    pluginEntitlements: parseEntitlements(row.plugin_entitlements),
    notes: row.notes,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    lastLoginAt: row.last_login_at,
    online,
    onlineSince: presence?.since ?? null,
    lastSeenAt: presence?.lastSeen ?? row.last_login_at,
    onlineSource: presence !== null ? 'presence' : directoryOnline ? 'directory' : 'none',
    stripeCustomerId: row.stripe_customer_id ?? null,
    stripeSubscriptionId: row.stripe_subscription_id ?? null,
    stripeSubscriptionStatus: row.stripe_subscription_status ?? null,
    stripeCurrentPeriodEnd: row.stripe_current_period_end ?? null,
    creditBalanceCents: row.credit_balance_cents ?? 0,
    presence: presence
      ? {
          ip: presence.ip,
          country: presence.country,
          platform: presence.platform,
          appVersion: presence.appVersion,
          radioBoard: presence.radioBoard,
          radioModel: presence.radioModel,
          radioConnected: presence.radioConnected,
        }
      : null,
  };
}

function toManagedPluginDto(row: ManagedPluginRow): ManagedPluginDto {
  return {
    pluginId: row.plugin_id,
    displayName: row.display_name || row.plugin_id,
    subscriptionRequired: row.subscription_required === 1,
    monthlyPriceCents: row.monthly_price_cents,
    currency: row.currency || 'USD',
    active: row.active === 1,
    checkoutUrl: row.checkout_url,
    notes: row.notes,
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    stripeProductId: row.stripe_product_id ?? null,
    stripePriceId: row.stripe_price_id ?? null,
    stripeSyncEnabled: row.subscription_required === 1 && row.monthly_price_cents > 0,
  };
}

function normalizedUserRow(callsign: string, body: Record<string, unknown>) {
  return {
    callsign,
    display_name: optionalString(body.displayName) ?? callsign,
    access_allowed: bool01(body.accessAllowed, true),
    is_admin: bool01(body.isAdmin, false),
    subscription_status: normalizedStatus(body.subscriptionStatus),
    subscription_expires_at: optionalNumber(body.subscriptionExpiresAt),
    plugin_access_mode: body.pluginAccessMode === 'selected' ? 'selected' : 'all',
    plugin_entitlements: stringifyEntitlements(body.pluginEntitlements),
    notes: optionalString(body.notes),
  };
}

function normalizedUserPatch(body: Record<string, unknown>) {
  const patch: Partial<ManagedUserRow> = {};
  if ('displayName' in body) patch.display_name = optionalString(body.displayName);
  if ('accessAllowed' in body) patch.access_allowed = bool01(body.accessAllowed, true);
  if ('isAdmin' in body) patch.is_admin = bool01(body.isAdmin, false);
  if ('subscriptionStatus' in body) patch.subscription_status = normalizedStatus(body.subscriptionStatus);
  if ('subscriptionExpiresAt' in body) patch.subscription_expires_at = optionalNumber(body.subscriptionExpiresAt);
  if ('pluginAccessMode' in body) patch.plugin_access_mode = body.pluginAccessMode === 'selected' ? 'selected' : 'all';
  if ('pluginEntitlements' in body) patch.plugin_entitlements = stringifyEntitlements(body.pluginEntitlements);
  if ('notes' in body) patch.notes = optionalString(body.notes);
  return patch;
}

function normalizedPluginRow(pluginId: string, body: Record<string, unknown>) {
  const cents = optionalNumber(body.monthlyPriceCents);
  return {
    plugin_id: pluginId,
    display_name: optionalString(body.displayName) ?? pluginId,
    subscription_required: bool01(body.subscriptionRequired, false),
    monthly_price_cents: Math.max(0, Math.round(cents ?? 0)),
    currency: (optionalString(body.currency) ?? 'USD').toUpperCase(),
    active: bool01(body.active, true),
    checkout_url: null,
    notes: optionalString(body.notes),
  };
}

function norm(callsign: string): string {
  return callsign.trim().toUpperCase();
}

function normPluginId(pluginId: string): string {
  return pluginId.trim().toLowerCase();
}

function optionalString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value.trim() : null;
}

function optionalNumber(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value === 'string' && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function bool01(value: unknown, fallback: boolean): number {
  if (typeof value === 'boolean') return value ? 1 : 0;
  return fallback ? 1 : 0;
}

function normalizedStatus(value: unknown): string {
  const status = optionalString(value)?.toLowerCase();
  return status || 'manual';
}

function parseEntitlements(raw: string): unknown[] {
  try {
    const parsed = JSON.parse(raw || '[]');
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function stringifyEntitlements(value: unknown): string {
  if (!Array.isArray(value)) return '[]';
  const entitlements = value
    .map((item) => {
      const raw = item && typeof item === 'object' ? (item as Record<string, unknown>) : {};
      const pluginId = normPluginId(String(raw.pluginId ?? ''));
      if (!pluginId) return null;
      return {
        pluginId,
        accessAllowed: raw.accessAllowed !== false,
        subscriptionStatus: normalizedStatus(raw.subscriptionStatus),
        subscriptionExpiresAt: optionalNumber(raw.subscriptionExpiresAt),
        denialReason: optionalString(raw.denialReason),
      };
    })
    .filter(Boolean);
  return JSON.stringify(entitlements);
}

async function safeJson(request: Request): Promise<Record<string, unknown>> {
  try {
    const v = await request.json();
    return v && typeof v === 'object' ? (v as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

function json(
  body: unknown,
  status: number,
  cors: Record<string, string>,
  extra?: Record<string, string>,
): Response {
  return Response.json(body, { status, headers: extra ? { ...cors, ...extra } : cors });
}

/**
 * Per-callsign login throttle, reusing the shared per-IP RateLimiter DO keyed by
 * `login:<callsign>`. Soft-limits password attempts against a single admin even
 * when an attacker rotates source IPs. Fails OPEN (returns false) if the limiter
 * is somehow unreachable — availability of login beats a hard dependency.
 */
async function loginRateLimited(env: Env, callsign: string): Promise<boolean> {
  try {
    const id = env.RATE_DO.idFromName(`login:${callsign}`);
    const res = await env.RATE_DO.get(id).fetch('https://rl.internal/check');
    return res.status === 429;
  } catch {
    return false;
  }
}

/**
 * CORS for the dashboard's cross-origin /admin fetches. The admin surface fails
 * CLOSED: an allowlist is built from `WEB_APP_ORIGIN` + `ADMIN_ORIGINS` and we
 * echo back ONLY the exact requesting origin when it matches — never '*' (unlike
 * /turn, which is public), and never a list (CORS permits a single origin value).
 * The maintainer dashboard runs at a different origin than the RX SPA, so naming
 * just `WEB_APP_ORIGIN` is not enough; `ADMIN_ORIGINS` carries the dashboard
 * origin(s). If neither is set, or the request's Origin is not allowlisted, no
 * Allow-Origin header is emitted, so a browser cannot read /admin responses
 * cross-origin — a misconfigured deploy can't silently expose the admin API to
 * every site. Authorization + QRZ headers are allowed; no cookies, so no
 * Allow-Credentials.
 */
export function corsHeaders(env: Env, request?: Request): Record<string, string> {
  const headers: Record<string, string> = {
    'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
    'Access-Control-Allow-Headers': 'content-type, authorization, x-qrz-session, x-qrz-callsign',
    'Access-Control-Max-Age': '86400',
    Vary: 'Origin',
  };
  const allowed = allowedAdminOrigins(env);
  const requestOrigin = request?.headers.get('Origin')?.trim();
  if (requestOrigin && allowed.has(requestOrigin)) {
    headers['Access-Control-Allow-Origin'] = requestOrigin;
  }
  return headers;
}

/**
 * The set of origins permitted to call /admin cross-origin: `WEB_APP_ORIGIN`
 * plus the comma-separated `ADMIN_ORIGINS`, trimmed and with any trailing slash
 * stripped (browsers send Origin without one). Empty entries are dropped.
 */
export function allowedAdminOrigins(env: Env): Set<string> {
  const out = new Set<string>();
  for (const raw of [env.WEB_APP_ORIGIN ?? '', ...(env.ADMIN_ORIGINS ?? '').split(',')]) {
    const o = raw.trim().replace(/\/+$/, '');
    if (o) out.add(o);
  }
  return out;
}
