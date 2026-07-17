import type { ManagedPluginRow, ManagedUserRow } from './admin-db';
import type { Env } from './types';

const STRIPE_API = 'https://api.stripe.com/v1';
const DEFAULT_WEBHOOK_TOLERANCE_MS = 5 * 60 * 1000;

export class StripeBillingError extends Error {
  readonly status: number;
  readonly detail: unknown;

  constructor(message: string, status = 500, detail?: unknown) {
    super(message);
    this.name = 'StripeBillingError';
    this.status = status;
    this.detail = detail;
  }
}

export type StripeCatalogSync = {
  productId: string;
  priceId: string;
  priceCents: number;
  priceCurrency: string;
};

export type StripeCheckoutResult = {
  url: string | null;
  id?: string;
};

export type StripeSubscriptionSnapshot = {
  id: string;
  customer: string;
  status: string;
  currentPeriodEndMs: number | null;
  priceIds: string[];
};

type StripeObject = Record<string, unknown>;

export function stripeConfigured(env: Env): boolean {
  return Boolean(env.STRIPE_SECRET_KEY?.trim());
}

export async function ensureStripeCatalogForPlugin(
  env: Env,
  plugin: ManagedPluginRow,
): Promise<StripeCatalogSync | null> {
  if (!stripeConfigured(env)) return null;
  const priceCents = Math.max(0, Math.round(plugin.monthly_price_cents));
  if (plugin.subscription_required !== 1 || priceCents <= 0) return null;

  const currency = (plugin.currency || 'USD').toLowerCase();
  const productId = plugin.stripe_product_id
    ? await updateStripeProduct(env, plugin)
    : await createStripeProduct(env, plugin);

  if (
    plugin.stripe_price_id &&
    plugin.stripe_price_cents === priceCents &&
    (plugin.stripe_price_currency || '').toLowerCase() === currency
  ) {
    return {
      productId,
      priceId: plugin.stripe_price_id,
      priceCents,
      priceCurrency: currency.toUpperCase(),
    };
  }

  if (plugin.stripe_price_id) {
    await stripeRequest(env, 'POST', `/prices/${encodeURIComponent(plugin.stripe_price_id)}`, {
      active: false,
    }).catch(() => undefined);
  }

  const price = await stripeRequest(env, 'POST', '/prices', {
    product: productId,
    unit_amount: priceCents,
    currency,
    recurring: { interval: 'month' },
    metadata: {
      zeus_plugin_id: plugin.plugin_id,
    },
  });
  const priceId = asString(price.id);
  if (!priceId) throw new StripeBillingError('Stripe price creation returned no id', 502, price);
  return {
    productId,
    priceId,
    priceCents,
    priceCurrency: currency.toUpperCase(),
  };
}

export async function createStripeCustomerForUser(
  env: Env,
  user: ManagedUserRow,
): Promise<string> {
  const customer = await stripeRequest(env, 'POST', '/customers', {
    name: user.display_name || user.callsign,
    metadata: { zeus_callsign: user.callsign },
  });
  const id = asString(customer.id);
  if (!id) throw new StripeBillingError('Stripe customer creation returned no id', 502, customer);
  return id;
}

export async function createStripeSubscriptionCheckout(
  env: Env,
  user: ManagedUserRow,
  customerId: string,
  plugins: ManagedPluginRow[],
  successUrl: string,
  cancelUrl: string,
): Promise<StripeCheckoutResult> {
  const params: StripeObject = {
    mode: 'subscription',
    customer: customerId,
    client_reference_id: user.callsign,
    success_url: successUrl,
    cancel_url: cancelUrl,
    metadata: {
      zeus_callsign: user.callsign,
      zeus_plugin_ids: plugins.map((p) => p.plugin_id).join(','),
    },
    subscription_data: {
      metadata: {
        zeus_callsign: user.callsign,
        zeus_plugin_ids: plugins.map((p) => p.plugin_id).join(','),
      },
    },
    line_items: plugins.map((plugin) => ({
      price: requiredPriceId(plugin),
      quantity: 1,
    })),
  };
  const session = await stripeRequest(env, 'POST', '/checkout/sessions', params);
  const url = nullableString(session.url);
  if (!url) throw new StripeBillingError('Stripe checkout session returned no url', 502, session);
  return { url, id: asString(session.id) };
}

export async function addPricesToStripeSubscription(
  env: Env,
  subscriptionId: string,
  priceIds: string[],
): Promise<void> {
  for (const priceId of priceIds) {
    await stripeRequest(env, 'POST', '/subscription_items', {
      subscription: subscriptionId,
      price: priceId,
      quantity: 1,
      proration_behavior: 'create_prorations',
    });
  }
}

export async function createStripeBillingPortalSession(
  env: Env,
  customerId: string,
  returnUrl: string,
): Promise<StripeCheckoutResult> {
  const session = await stripeRequest(env, 'POST', '/billing_portal/sessions', {
    customer: customerId,
    return_url: returnUrl,
  });
  return { url: nullableString(session.url), id: asString(session.id) };
}

export async function retrieveStripeSubscription(
  env: Env,
  subscriptionId: string,
): Promise<StripeSubscriptionSnapshot> {
  const sub = await stripeRequest(env, 'GET', `/subscriptions/${encodeURIComponent(subscriptionId)}`, {
    expand: ['items.data.price'],
  });
  return parseStripeSubscription(sub);
}

export async function grantStripeCustomerCredit(
  env: Env,
  customerId: string,
  amountCents: number,
  currency: string,
  description?: string | null,
): Promise<void> {
  if (!stripeConfigured(env)) return;
  const cents = Math.max(0, Math.round(amountCents));
  if (cents <= 0) return;
  await stripeRequest(env, 'POST', `/customers/${encodeURIComponent(customerId)}/balance_transactions`, {
    amount: -cents,
    currency: currency.toLowerCase(),
    description: description || 'Zeus plugin subscription credit',
  });
}

export async function verifyStripeWebhookSignature(
  payload: string,
  signatureHeader: string | null,
  secret: string | undefined,
  now = Date.now(),
  toleranceMs = DEFAULT_WEBHOOK_TOLERANCE_MS,
): Promise<boolean> {
  const trimmedSecret = secret?.trim();
  if (!trimmedSecret || !signatureHeader) return false;
  const parts = signatureHeader.split(',').map((p) => p.trim());
  const timestamp = parts.find((p) => p.startsWith('t='))?.slice(2);
  const signatures = parts.filter((p) => p.startsWith('v1=')).map((p) => p.slice(3));
  const ts = timestamp ? Number(timestamp) : NaN;
  if (!Number.isFinite(ts) || signatures.length === 0) return false;
  if (Math.abs(now - ts * 1000) > toleranceMs) return false;
  const expected = await hmacSha256Hex(trimmedSecret, `${timestamp}.${payload}`);
  return signatures.some((sig) => constantTimeHexEqual(sig, expected));
}

export function parseStripeSubscription(raw: StripeObject): StripeSubscriptionSnapshot {
  const customer = objectId(raw.customer);
  const currentPeriodEnd = asNumber(raw.current_period_end);
  const items = asArray((raw.items as StripeObject | undefined)?.data);
  return {
    id: asString(raw.id),
    customer,
    status: asString(raw.status) || 'unknown',
    currentPeriodEndMs: currentPeriodEnd ? currentPeriodEnd * 1000 : null,
    priceIds: items.map((item) => objectId((item as StripeObject).price)).filter(Boolean),
  };
}

export function stripeDefaultSuccessUrl(env: Env): string {
  return env.STRIPE_SUCCESS_URL?.trim()
    || `${env.WEB_APP_ORIGIN ?? 'https://app.openhpsdrzeus.com'}/settings/plugins?billing=success`;
}

export function stripeDefaultCancelUrl(env: Env): string {
  return env.STRIPE_CANCEL_URL?.trim()
    || `${env.WEB_APP_ORIGIN ?? 'https://app.openhpsdrzeus.com'}/settings/plugins?billing=cancel`;
}

async function createStripeProduct(env: Env, plugin: ManagedPluginRow): Promise<string> {
  const product = await stripeRequest(env, 'POST', '/products', productParams(plugin));
  const id = asString(product.id);
  if (!id) throw new StripeBillingError('Stripe product creation returned no id', 502, product);
  return id;
}

async function updateStripeProduct(env: Env, plugin: ManagedPluginRow): Promise<string> {
  const productId = plugin.stripe_product_id || '';
  const product = await stripeRequest(env, 'POST', `/products/${encodeURIComponent(productId)}`, productParams(plugin));
  const id = asString(product.id);
  if (!id) throw new StripeBillingError('Stripe product update returned no id', 502, product);
  return id;
}

function productParams(plugin: ManagedPluginRow): StripeObject {
  return {
    name: plugin.display_name || plugin.plugin_id,
    active: plugin.active === 1,
    metadata: {
      zeus_plugin_id: plugin.plugin_id,
    },
  };
}

async function stripeRequest(
  env: Env,
  method: 'GET' | 'POST',
  path: string,
  params?: StripeObject,
): Promise<StripeObject> {
  const secret = env.STRIPE_SECRET_KEY?.trim();
  if (!secret) throw new StripeBillingError('Stripe billing is not configured', 503);

  const query = method === 'GET' && params ? `?${stripeForm(params).toString()}` : '';
  const headers = new Headers({
    authorization: `Basic ${base64(`${secret}:`)}`,
  });
  let body: URLSearchParams | undefined;
  if (method === 'POST') {
    headers.set('content-type', 'application/x-www-form-urlencoded');
    body = stripeForm(params ?? {});
  }

  const res = await fetch(`${STRIPE_API}${path}${query}`, { method, headers, body });
  const text = await res.text();
  let parsed: unknown = null;
  if (text) {
    try { parsed = JSON.parse(text) as unknown; } catch { parsed = text; }
  }
  if (!res.ok) {
    const error = parsed && typeof parsed === 'object' ? (parsed as StripeObject).error as StripeObject | undefined : undefined;
    const message = asString(error?.message) || `${res.status} ${res.statusText}`;
    throw new StripeBillingError(message, res.status, parsed);
  }
  return parsed && typeof parsed === 'object' ? (parsed as StripeObject) : {};
}

export function stripeForm(params: StripeObject): URLSearchParams {
  const form = new URLSearchParams();
  appendStripeParam(form, '', params);
  return form;
}

function appendStripeParam(form: URLSearchParams, prefix: string, value: unknown): void {
  if (value === null || value === undefined) return;
  if (Array.isArray(value)) {
    value.forEach((item, index) => appendStripeParam(form, `${prefix}[${index}]`, item));
    return;
  }
  if (typeof value === 'object') {
    for (const [key, child] of Object.entries(value as StripeObject)) {
      appendStripeParam(form, prefix ? `${prefix}[${key}]` : key, child);
    }
    return;
  }
  form.append(prefix, typeof value === 'boolean' ? (value ? 'true' : 'false') : String(value));
}

function requiredPriceId(plugin: ManagedPluginRow): string {
  if (!plugin.stripe_price_id) {
    throw new StripeBillingError(`Plugin ${plugin.plugin_id} has no Stripe price`, 409);
  }
  return plugin.stripe_price_id;
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function nullableString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

function asNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function objectId(value: unknown): string {
  if (typeof value === 'string') return value;
  if (value && typeof value === 'object') return asString((value as StripeObject).id);
  return '';
}

function base64(value: string): string {
  return btoa(value);
}

async function hmacSha256Hex(secret: string, value: string): Promise<string> {
  const enc = new TextEncoder();
  const key = await crypto.subtle.importKey(
    'raw',
    enc.encode(secret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const signed = await crypto.subtle.sign('HMAC', key, enc.encode(value));
  return [...new Uint8Array(signed)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

function constantTimeHexEqual(a: string, b: string): boolean {
  if (!/^[0-9a-f]+$/i.test(a) || !/^[0-9a-f]+$/i.test(b)) return false;
  const aa = a.toLowerCase();
  const bb = b.toLowerCase();
  let diff = aa.length ^ bb.length;
  const len = Math.max(aa.length, bb.length);
  for (let i = 0; i < len; i++) {
    diff |= (aa.charCodeAt(i) || 0) ^ (bb.charCodeAt(i) || 0);
  }
  return diff === 0;
}
