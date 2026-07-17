import {
  getManagedUser,
  getManagedUserByStripeCustomer,
  listManagedPlugins,
  recordManagedUserLogin,
  setManagedPluginStripeCatalog,
  setManagedUserStripeCustomer,
  setManagedUserStripeSubscription,
  upsertManagedUser,
  type ManagedPluginRow,
  type ManagedUserRow,
} from './admin-db';
import { mergedManagedPlugins } from './admin-api';
import {
  addPricesToStripeSubscription,
  createStripeBillingPortalSession,
  createStripeCustomerForUser,
  createStripeSubscriptionCheckout,
  ensureStripeCatalogForPlugin,
  parseStripeSubscription,
  retrieveStripeSubscription,
  stripeConfigured,
  stripeDefaultCancelUrl,
  stripeDefaultSuccessUrl,
  verifyStripeWebhookSignature,
  type StripeSubscriptionSnapshot,
} from './stripe-billing';
import type { Env } from './types';

const STRIPE_ENTITLEMENT_STATUSES = new Set([
  'active',
  'trialing',
  'past_due',
  'canceled',
  'incomplete',
  'incomplete_expired',
  'unpaid',
  'paused',
]);

type JsonRecord = Record<string, unknown>;

export async function handleUserSession(
  verifiedCallsign: string,
  env: Env,
): Promise<Response> {
  await recordManagedUserLogin(env.ADMIN_DB, verifiedCallsign);
  const user = await getManagedUser(env.ADMIN_DB, verifiedCallsign);
  return Response.json({
    callsign: verifiedCallsign,
    user: user ? managedUserDto(user) : null,
    plugins: await mergedManagedPlugins(env),
    pluginRegistryUrl: env.PLUGIN_REGISTRY_URL ?? null,
  }, {
    headers: { 'Cache-Control': 'no-store' },
  });
}

export async function handleBillingCheckout(
  request: Request,
  env: Env,
  verifiedCallsign: string,
): Promise<Response> {
  if (!stripeConfigured(env)) return json({ error: 'Stripe billing is not configured' }, 503);

  const body = await safeJson(request);
  const pluginIds = normalizePluginIds([
    ...asStringArray(body.pluginIds),
    typeof body.pluginId === 'string' ? body.pluginId : '',
  ]);
  if (pluginIds.length === 0) return json({ error: 'pluginId required' }, 400);
  if (pluginIds.length > 20) return json({ error: 'Stripe subscriptions support at most 20 plugin prices' }, 400);

  await recordManagedUserLogin(env.ADMIN_DB, verifiedCallsign);
  let user = await getManagedUser(env.ADMIN_DB, verifiedCallsign)
    ?? await upsertManagedUser(env.ADMIN_DB, { callsign: verifiedCallsign });
  if (user.access_allowed !== 1) return json({ error: 'Zeus app access disabled by admin' }, 403);

  const allPlugins = await listManagedPlugins(env.ADMIN_DB);
  const byId = new Map(allPlugins.map((plugin) => [plugin.plugin_id, plugin]));
  const requested = pluginIds.map((id) => byId.get(id)).filter((p): p is ManagedPluginRow => p !== undefined);
  if (requested.length !== pluginIds.length) return json({ error: 'plugin is not managed by admin billing' }, 404);

  const paid = requested.filter((plugin) =>
    plugin.active === 1 &&
    plugin.subscription_required === 1 &&
    plugin.monthly_price_cents > 0);
  if (paid.length === 0) return json({ url: null, subscriptionUpdated: false, free: true }, 200);

  const synced = await Promise.all(paid.map((plugin) => syncStripePlugin(env, plugin)));
  const customerId = await ensureCustomer(env, user);
  if (customerId !== user.stripe_customer_id) {
    user = await getManagedUser(env.ADMIN_DB, verifiedCallsign) ?? user;
  }

  const successUrl = validAbsoluteUrl(body.successUrl) ?? validAbsoluteUrl(body.returnUrl) ?? stripeDefaultSuccessUrl(env);
  const cancelUrl = validAbsoluteUrl(body.cancelUrl) ?? validAbsoluteUrl(body.returnUrl) ?? stripeDefaultCancelUrl(env);
  const returnUrl = validAbsoluteUrl(body.returnUrl) ?? successUrl;

  const existingSubscriptionId = user.stripe_subscription_id;
  if (!existingSubscriptionId) {
    const checkout = await createStripeSubscriptionCheckout(env, user, customerId, synced, successUrl, cancelUrl);
    return json({ url: checkout.url, subscriptionUpdated: false, pluginIds: synced.map((p) => p.plugin_id) }, 200);
  }

  let subscription: StripeSubscriptionSnapshot | null = null;
  try {
    subscription = await retrieveStripeSubscription(env, existingSubscriptionId);
  } catch {
    subscription = null;
  }
  if (!subscription || subscription.status === 'canceled' || subscription.status === 'incomplete_expired') {
    const checkout = await createStripeSubscriptionCheckout(env, user, customerId, synced, successUrl, cancelUrl);
    return json({ url: checkout.url, subscriptionUpdated: false, pluginIds: synced.map((p) => p.plugin_id) }, 200);
  }

  const existingPrices = new Set(subscription.priceIds);
  const missingPrices = synced.map((p) => p.stripe_price_id).filter((price): price is string =>
    typeof price === 'string' && price.length > 0 && !existingPrices.has(price));
  if (missingPrices.length > 0) {
    if (subscription.priceIds.length + missingPrices.length > 20) {
      return json({ error: 'Stripe subscriptions support at most 20 plugin prices' }, 409);
    }
    await addPricesToStripeSubscription(env, subscription.id, missingPrices);
    subscription = await retrieveStripeSubscription(env, subscription.id);
  }
  await applySubscriptionToUser(env, user.callsign, subscription);

  const portal = await createStripeBillingPortalSession(env, customerId, returnUrl).catch(() => ({ url: null }));
  return json({
    url: portal.url,
    subscriptionUpdated: true,
    pluginIds: synced.map((p) => p.plugin_id),
  }, 200);
}

export async function handleBillingWebhook(request: Request, env: Env): Promise<Response> {
  const payload = await request.text();
  const ok = await verifyStripeWebhookSignature(
    payload,
    request.headers.get('Stripe-Signature'),
    env.STRIPE_WEBHOOK_SECRET,
  );
  if (!ok) return json({ error: 'invalid signature' }, 400);

  let event: JsonRecord;
  try {
    event = JSON.parse(payload) as JsonRecord;
  } catch {
    return json({ error: 'invalid payload' }, 400);
  }

  const type = typeof event.type === 'string' ? event.type : '';
  const data = event.data && typeof event.data === 'object' ? event.data as JsonRecord : {};
  const object = data.object && typeof data.object === 'object' ? data.object as JsonRecord : {};

  if (type === 'checkout.session.completed') {
    const customerId = objectId(object.customer);
    const subscriptionId = objectId(object.subscription);
    if (customerId && subscriptionId) {
      const user = await getManagedUserByStripeCustomer(env.ADMIN_DB, customerId)
        ?? await userFromStripeMetadata(env, object);
      if (user) {
        const subscription = await retrieveStripeSubscription(env, subscriptionId);
        await applySubscriptionToUser(env, user.callsign, subscription);
      }
    }
    return json({ ok: true }, 200);
  }

  if (type === 'invoice.paid' || type === 'invoice.payment_succeeded') {
    const customerId = objectId(object.customer);
    const subscriptionId = stripeInvoiceSubscriptionId(object);
    if (subscriptionId) {
      const user = customerId
        ? await getManagedUserByStripeCustomer(env.ADMIN_DB, customerId)
          ?? await userFromStripeMetadata(env, object)
        : await userFromStripeMetadata(env, object);
      if (user) {
        const subscription = await retrieveStripeSubscription(env, subscriptionId);
        await applySubscriptionToUser(env, user.callsign, subscription);
      }
    }
    return json({ ok: true }, 200);
  }

  if (type === 'customer.subscription.created'
      || type === 'customer.subscription.updated'
      || type === 'customer.subscription.deleted') {
    const subscription = parseStripeSubscription(object);
    if (subscription.customer) {
      const user = await getManagedUserByStripeCustomer(env.ADMIN_DB, subscription.customer)
        ?? await userFromStripeMetadata(env, object);
      if (user) await applySubscriptionToUser(env, user.callsign, subscription);
    }
    return json({ ok: true }, 200);
  }

  return json({ ok: true, ignored: true }, 200);
}

async function syncStripePlugin(env: Env, plugin: ManagedPluginRow): Promise<ManagedPluginRow> {
  const sync = await ensureStripeCatalogForPlugin(env, plugin);
  if (!sync) return plugin;
  return await setManagedPluginStripeCatalog(env.ADMIN_DB, plugin.plugin_id, {
    stripe_product_id: sync.productId,
    stripe_price_id: sync.priceId,
    stripe_price_cents: sync.priceCents,
    stripe_price_currency: sync.priceCurrency,
  }) ?? plugin;
}

async function ensureCustomer(env: Env, user: ManagedUserRow): Promise<string> {
  if (user.stripe_customer_id) return user.stripe_customer_id;
  const customerId = await createStripeCustomerForUser(env, user);
  await setManagedUserStripeCustomer(env.ADMIN_DB, user.callsign, customerId);
  return customerId;
}

async function applySubscriptionToUser(
  env: Env,
  callsign: string,
  subscription: StripeSubscriptionSnapshot,
): Promise<ManagedUserRow | null> {
  const user = await getManagedUser(env.ADMIN_DB, callsign);
  if (!user) return null;

  const plugins = await listManagedPlugins(env.ADMIN_DB);
  const byPrice = new Map(plugins
    .filter((plugin) => plugin.stripe_price_id)
    .map((plugin) => [plugin.stripe_price_id as string, plugin.plugin_id]));
  const subscriptionPluginIds = subscription.priceIds
    .map((priceId) => byPrice.get(priceId))
    .filter((pluginId): pluginId is string => Boolean(pluginId));
  const entitlements = mergeSubscriptionEntitlements(
    parseEntitlements(user.plugin_entitlements),
    subscriptionPluginIds,
    subscription.status,
    subscription.currentPeriodEndMs,
  );

  return setManagedUserStripeSubscription(env.ADMIN_DB, callsign, {
    stripe_subscription_id: subscription.id,
    stripe_subscription_status: subscription.status,
    stripe_current_period_end: subscription.currentPeriodEndMs,
    plugin_entitlements: JSON.stringify(entitlements),
  });
}

export function mergeSubscriptionEntitlements(
  existing: PluginEntitlement[],
  pluginIds: string[],
  status: string,
  expiresAt: number | null,
): PluginEntitlement[] {
  const active = status === 'active' || status === 'trialing';
  const wanted = new Set(pluginIds.map(normPluginId));
  const byId = new Map(existing.map((entitlement) => [normPluginId(entitlement.pluginId), entitlement]));

  for (const pluginId of wanted) {
    const current = byId.get(pluginId);
    const manualDeny = current?.accessAllowed === false
      && !STRIPE_ENTITLEMENT_STATUSES.has((current.subscriptionStatus || '').toLowerCase());
    if (manualDeny) continue;
    byId.set(pluginId, {
      pluginId,
      accessAllowed: active,
      subscriptionStatus: status,
      subscriptionExpiresAt: expiresAt,
      denialReason: active ? null : `Stripe subscription status: ${status}`,
    });
  }

  return [...byId.values()].sort((a, b) => a.pluginId.localeCompare(b.pluginId));
}

export function stripeInvoiceSubscriptionId(invoice: Record<string, unknown>): string {
  const direct = objectId(invoice.subscription);
  if (direct) return direct;

  const parent = invoice.parent && typeof invoice.parent === 'object'
    ? invoice.parent as JsonRecord
    : {};
  const parentSubscription = subscriptionIdFromInvoiceParent(parent);
  if (parentSubscription) return parentSubscription;

  const lines = invoice.lines && typeof invoice.lines === 'object'
    ? (invoice.lines as JsonRecord).data
    : null;
  if (!Array.isArray(lines)) return '';
  for (const line of lines) {
    if (!line || typeof line !== 'object') continue;
    const row = line as JsonRecord;
    const lineSubscription = objectId(row.subscription);
    if (lineSubscription) return lineSubscription;
    const lineParent = row.parent && typeof row.parent === 'object'
      ? row.parent as JsonRecord
      : {};
    const nested = subscriptionIdFromInvoiceParent(lineParent);
    if (nested) return nested;
  }
  return '';
}

function subscriptionIdFromInvoiceParent(parent: JsonRecord): string {
  const details = parent.subscription_item_details && typeof parent.subscription_item_details === 'object'
    ? parent.subscription_item_details as JsonRecord
    : {};
  return objectId(details.subscription);
}

function managedUserDto(row: ManagedUserRow) {
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
    stripeCustomerId: row.stripe_customer_id ?? null,
    stripeSubscriptionId: row.stripe_subscription_id ?? null,
    stripeSubscriptionStatus: row.stripe_subscription_status ?? null,
    stripeCurrentPeriodEnd: row.stripe_current_period_end ?? null,
    creditBalanceCents: row.credit_balance_cents ?? 0,
  };
}

type PluginEntitlement = {
  pluginId: string;
  accessAllowed: boolean;
  subscriptionStatus: string;
  subscriptionExpiresAt: number | null;
  denialReason: string | null;
};

function parseEntitlements(raw: string): PluginEntitlement[] {
  try {
    const parsed = JSON.parse(raw || '[]') as unknown;
    if (!Array.isArray(parsed)) return [];
    return parsed
      .map((item) => {
        const row = item && typeof item === 'object' ? item as JsonRecord : {};
        const pluginId = normPluginId(String(row.pluginId ?? ''));
        if (!pluginId) return null;
        return {
          pluginId,
          accessAllowed: row.accessAllowed === true,
          subscriptionStatus: typeof row.subscriptionStatus === 'string' ? row.subscriptionStatus : 'manual',
          subscriptionExpiresAt: typeof row.subscriptionExpiresAt === 'number' ? row.subscriptionExpiresAt : null,
          denialReason: typeof row.denialReason === 'string' ? row.denialReason : null,
        };
      })
      .filter((item): item is PluginEntitlement => item !== null);
  } catch {
    return [];
  }
}

async function userFromStripeMetadata(env: Env, object: JsonRecord): Promise<ManagedUserRow | null> {
  const metadata = object.metadata && typeof object.metadata === 'object' ? object.metadata as JsonRecord : {};
  const callsign = normCallsign(String(metadata.zeus_callsign ?? ''));
  return callsign ? getManagedUser(env.ADMIN_DB, callsign) : null;
}

function normalizePluginIds(raw: string[]): string[] {
  return [...new Set(raw.map(normPluginId).filter(Boolean))];
}

function normPluginId(pluginId: string): string {
  return pluginId.trim().toLowerCase();
}

function normCallsign(callsign: string): string {
  return callsign.trim().toUpperCase();
}

function asStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];
}

function validAbsoluteUrl(value: unknown): string | null {
  if (typeof value !== 'string' || !value.trim()) return null;
  try {
    const url = new URL(value.trim());
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.toString() : null;
  } catch {
    return null;
  }
}

function objectId(value: unknown): string {
  if (typeof value === 'string') return value;
  if (value && typeof value === 'object') {
    const id = (value as JsonRecord).id;
    return typeof id === 'string' ? id : '';
  }
  return '';
}

async function safeJson(request: Request): Promise<JsonRecord> {
  try {
    const raw = await request.json();
    return raw && typeof raw === 'object' ? raw as JsonRecord : {};
  } catch {
    return {};
  }
}

function json(body: unknown, status: number): Response {
  return Response.json(body, { status, headers: { 'Cache-Control': 'no-store' } });
}
