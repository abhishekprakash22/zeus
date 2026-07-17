-- Stripe-backed plugin subscription management.
-- Admin-managed plugin pricing is the catalog source of truth; Stripe IDs are
-- cached here so app checkout can add multiple paid plugins to one subscription.

ALTER TABLE managed_plugins ADD COLUMN stripe_product_id TEXT;
ALTER TABLE managed_plugins ADD COLUMN stripe_price_id TEXT;
ALTER TABLE managed_plugins ADD COLUMN stripe_price_cents INTEGER;
ALTER TABLE managed_plugins ADD COLUMN stripe_price_currency TEXT;

ALTER TABLE admin_users ADD COLUMN stripe_customer_id TEXT;
ALTER TABLE admin_users ADD COLUMN stripe_subscription_id TEXT;
ALTER TABLE admin_users ADD COLUMN stripe_subscription_status TEXT;
ALTER TABLE admin_users ADD COLUMN stripe_current_period_end INTEGER;
ALTER TABLE admin_users ADD COLUMN credit_balance_cents INTEGER NOT NULL DEFAULT 0;

CREATE INDEX admin_users_stripe_customer ON admin_users(stripe_customer_id);
CREATE INDEX admin_users_stripe_subscription ON admin_users(stripe_subscription_id);
CREATE INDEX managed_plugins_stripe_price ON managed_plugins(stripe_price_id);
