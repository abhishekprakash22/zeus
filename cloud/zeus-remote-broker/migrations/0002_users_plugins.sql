-- User/subscription management for the public maintainer admin dashboard.
-- The broker-side store is the durable remote admin ledger used by
-- https://openhpsdrzeus.com/admin/.

CREATE TABLE admin_users (
  callsign                  TEXT PRIMARY KEY,        -- uppercased QRZ callsign
  display_name              TEXT,
  access_allowed            INTEGER NOT NULL DEFAULT 1,
  is_admin                  INTEGER NOT NULL DEFAULT 0,
  subscription_status       TEXT NOT NULL DEFAULT 'manual',
  subscription_expires_at   INTEGER,                 -- unix ms, nullable
  plugin_access_mode        TEXT NOT NULL DEFAULT 'all',
  plugin_entitlements       TEXT NOT NULL DEFAULT '[]', -- JSON array
  notes                     TEXT,
  created_at                INTEGER NOT NULL,
  updated_at                INTEGER NOT NULL,
  last_login_at             INTEGER
);

CREATE TABLE managed_plugins (
  plugin_id                 TEXT PRIMARY KEY,        -- lowercased plugin id
  display_name              TEXT NOT NULL,
  subscription_required     INTEGER NOT NULL DEFAULT 0,
  monthly_price_cents       INTEGER NOT NULL DEFAULT 0,
  currency                  TEXT NOT NULL DEFAULT 'USD',
  active                    INTEGER NOT NULL DEFAULT 1,
  checkout_url              TEXT,
  notes                     TEXT,
  created_at                INTEGER NOT NULL,
  updated_at                INTEGER NOT NULL
);

CREATE INDEX admin_users_access ON admin_users(access_allowed);
CREATE INDEX admin_users_subscription ON admin_users(subscription_status);
CREATE INDEX managed_plugins_active ON managed_plugins(active);
