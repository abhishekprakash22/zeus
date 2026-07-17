import { createHmac } from 'node:crypto';
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  parseStripeSubscription,
  stripeForm,
  verifyStripeWebhookSignature,
} from '../src/stripe-billing.ts';
import {
  mergeSubscriptionEntitlements,
  stripeInvoiceSubscriptionId,
} from '../src/billing-api.ts';

test('stripeForm encodes nested line items using Stripe bracket notation', () => {
  const form = stripeForm({
    mode: 'subscription',
    line_items: [
      { price: 'price_one', quantity: 1 },
      { price: 'price_two', quantity: 1 },
    ],
    subscription_data: {
      metadata: {
        zeus_callsign: 'N9WAR',
      },
    },
  });

  assert.equal(form.get('mode'), 'subscription');
  assert.equal(form.get('line_items[0][price]'), 'price_one');
  assert.equal(form.get('line_items[1][price]'), 'price_two');
  assert.equal(form.get('subscription_data[metadata][zeus_callsign]'), 'N9WAR');
});

test('parseStripeSubscription maps expanded subscription item prices', () => {
  const parsed = parseStripeSubscription({
    id: 'sub_123',
    customer: { id: 'cus_123' },
    status: 'active',
    current_period_end: 1_772_000_000,
    items: {
      data: [
        { price: { id: 'price_a' } },
        { price: 'price_b' },
      ],
    },
  });

  assert.deepEqual(parsed, {
    id: 'sub_123',
    customer: 'cus_123',
    status: 'active',
    currentPeriodEndMs: 1_772_000_000_000,
    priceIds: ['price_a', 'price_b'],
  });
});

test('stripeInvoiceSubscriptionId reads direct and nested invoice subscription ids', () => {
  assert.equal(
    stripeInvoiceSubscriptionId({ subscription: 'sub_direct' }),
    'sub_direct',
  );
  assert.equal(
    stripeInvoiceSubscriptionId({
      lines: {
        data: [
          {
            parent: {
              subscription_item_details: {
                subscription: 'sub_nested',
              },
            },
          },
        ],
      },
    }),
    'sub_nested',
  );
});

test('mergeSubscriptionEntitlements extends expiration on paid subscription renewal', () => {
  const oldExpiry = 1_772_000_000_000;
  const renewedExpiry = 1_774_678_400_000;

  const entitlements = mergeSubscriptionEntitlements(
    [
      {
        pluginId: 'com.openhpsdr.paid',
        accessAllowed: true,
        subscriptionStatus: 'active',
        subscriptionExpiresAt: oldExpiry,
        denialReason: null,
      },
    ],
    ['com.openhpsdr.paid'],
    'active',
    renewedExpiry,
  );

  assert.deepEqual(entitlements, [
    {
      pluginId: 'com.openhpsdr.paid',
      accessAllowed: true,
      subscriptionStatus: 'active',
      subscriptionExpiresAt: renewedExpiry,
      denialReason: null,
    },
  ]);
});

test('verifyStripeWebhookSignature accepts valid v1 signature and rejects stale signatures', async () => {
  const secret = 'whsec_test';
  const payload = JSON.stringify({ id: 'evt_123', type: 'checkout.session.completed' });
  const timestamp = 1_772_000_000;
  const signature = createHmac('sha256', secret)
    .update(`${timestamp}.${payload}`)
    .digest('hex');

  assert.equal(
    await verifyStripeWebhookSignature(payload, `t=${timestamp},v1=${signature}`, secret, timestamp * 1000),
    true,
  );
  assert.equal(
    await verifyStripeWebhookSignature(payload, `t=${timestamp - 600},v1=${signature}`, secret, timestamp * 1000),
    false,
  );
});
