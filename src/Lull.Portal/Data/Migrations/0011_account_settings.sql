-- One row (id = 1): how people sign in and how long the audit log is kept
-- (AccountSettings, as JSON), once the owner saves them in Settings. No row:
-- the values in the deployment's .env.
CREATE TABLE IF NOT EXISTS account_settings (
    id         int PRIMARY KEY CHECK (id = 1),
    settings   jsonb NOT NULL,
    updated_by text NOT NULL,
    updated_at timestamptz NOT NULL
);
