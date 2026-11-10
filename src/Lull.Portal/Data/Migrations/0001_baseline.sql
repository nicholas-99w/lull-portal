-- Baseline: the schema as the portal created it before migrations, unchanged.
-- Every statement is safe to re-run, so this also applies cleanly to a database
-- created by an older portal (its data fixes then run one last time). Never edit
-- this file once released: put changes in a new, higher-numbered script.

CREATE TABLE IF NOT EXISTS devices (
    id               uuid PRIMARY KEY,
    serial_number    text NOT NULL UNIQUE,
    hardware_uuid    text NOT NULL,
    token_hash       bytea NOT NULL UNIQUE,
    -- Pinned from the first non-exempt user in a check-in; IT can change it.
    primary_username text,
    -- Set by IT's "Reset device": allows one re-registration of this serial.
    reset_allowed    boolean NOT NULL DEFAULT false,
    registered_at    timestamptz NOT NULL,
    last_checkin_at  timestamptz,
    last_report      jsonb
);
CREATE INDEX IF NOT EXISTS devices_primary_username ON devices (primary_username);
-- Set when IT de-registers the Mac. The row stays as a tombstone so the
-- old token gets 410 Gone (the Mac then falls back to its profile).
ALTER TABLE devices ADD COLUMN IF NOT EXISTS deregistered_at timestamptz;

CREATE TABLE IF NOT EXISTS device_settings (
    device_id     uuid PRIMARY KEY REFERENCES devices (id) ON DELETE CASCADE,
    enabled       boolean NOT NULL,
    school_start  time NOT NULL,
    school_end    time NOT NULL,
    weekend_start time NOT NULL,
    weekend_end   time NOT NULL,
    revision      bigint NOT NULL,
    updated_by    text NOT NULL,
    updated_at    timestamptz NOT NULL
);
-- "Lock down now" (added later: ALTER keeps existing databases working).
ALTER TABLE device_settings ADD COLUMN IF NOT EXISTS lock_from timestamptz;
ALTER TABLE device_settings ADD COLUMN IF NOT EXISTS lock_until timestamptz;
-- "Release now": lulld ends the downtime window in progress at this instant.
ALTER TABLE device_settings ADD COLUMN IF NOT EXISTS released_at timestamptz;
-- NULL windows = follow the school's (Jamf profile's) schedule for those days.
ALTER TABLE device_settings ALTER COLUMN school_start DROP NOT NULL;
ALTER TABLE device_settings ALTER COLUMN school_end DROP NOT NULL;
ALTER TABLE device_settings ALTER COLUMN weekend_start DROP NOT NULL;
ALTER TABLE device_settings ALTER COLUMN weekend_end DROP NOT NULL;
-- Per-kind opt-out (school nights / weekend), replacing the all-or-nothing
-- "enabled = false". Macs opted out the old way move to both kinds off,
-- with a new revision so lulld accepts it. Idempotent: enabled is true after.
ALTER TABLE device_settings ADD COLUMN IF NOT EXISTS school_off boolean NOT NULL DEFAULT false;
ALTER TABLE device_settings ADD COLUMN IF NOT EXISTS weekend_off boolean NOT NULL DEFAULT false;
UPDATE device_settings
SET enabled = true, school_off = true, weekend_off = true,
    school_start = NULL, school_end = NULL, weekend_start = NULL, weekend_end = NULL,
    revision = GREATEST(revision + 1, (extract(epoch FROM now()) * 1000)::bigint), updated_at = now()
WHERE NOT enabled;
-- Macs that never had a person change their times got the portal's old
-- defaults: switch them to following the school. A new revision makes
-- lulld accept it (an unchanged one with new content would be refused).
-- Idempotent: system-written rows are NULL from now on.
UPDATE device_settings
SET school_start = NULL, school_end = NULL, weekend_start = NULL, weekend_end = NULL,
    revision = GREATEST(revision + 1, (extract(epoch FROM now()) * 1000)::bigint),
    updated_by = 'system (follow school)', updated_at = now()
WHERE updated_by = 'system' AND (school_start IS NOT NULL OR weekend_start IS NOT NULL);

-- One row (id = 1): IT's school hours and lock-down rules, as JSON (BoundsPolicy).
CREATE TABLE IF NOT EXISTS bounds_policy (
    id         int PRIMARY KEY CHECK (id = 1),
    policy     jsonb NOT NULL,
    updated_by text NOT NULL,
    updated_at timestamptz NOT NULL
);

-- Portal accounts: parents and IT admins. Parents sign in with a password they
-- set on first sign-in, after proving who they are with a one-time setup
-- code from IT. Secrets are stored hashed only.
CREATE TABLE IF NOT EXISTS users (
    id                 uuid PRIMARY KEY,
    email              text NOT NULL UNIQUE,      -- lower-case; the username
    display_name       text NOT NULL,
    role               text NOT NULL CHECK (role IN ('Parent', 'Admin')),
    password_hash      text,                      -- NULL until set (PBKDF2)
    setup_code_hash    bytea,                     -- NULL once used (SHA-256)
    setup_code_expires timestamptz,
    failed_attempts    int NOT NULL DEFAULT 0,
    locked_until       timestamptz,
    -- Changes whenever sessions must end (password reset, role/email change, delete).
    security_stamp     uuid NOT NULL,
    created_at         timestamptz NOT NULL,
    updated_at         timestamptz NOT NULL,
    last_login_at      timestamptz
);
-- A parent's children, by school username (matched to a Mac's pinned primary user).
CREATE TABLE IF NOT EXISTS user_students (
    user_id  uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    username text NOT NULL,
    PRIMARY KEY (user_id, username)
);
CREATE INDEX IF NOT EXISTS user_students_username ON user_students (username);
-- The owner: the admin created on the portal's first run. Exactly one,
-- never deleted or demoted. An existing portal's earliest admin becomes it.
ALTER TABLE users ADD COLUMN IF NOT EXISTS is_owner boolean NOT NULL DEFAULT false;
CREATE UNIQUE INDEX IF NOT EXISTS users_one_owner ON users (is_owner) WHERE is_owner;
UPDATE users SET is_owner = true
WHERE id = (SELECT id FROM users WHERE role = 'Admin' ORDER BY created_at, email LIMIT 1)
  AND NOT EXISTS (SELECT 1 FROM users WHERE is_owner);
-- The proof-of-concept list parents typed in themselves; replaced by user_students.
DROP TABLE IF EXISTS parent_children;

CREATE TABLE IF NOT EXISTS audit_log (
    id        bigserial PRIMARY KEY,
    at        timestamptz NOT NULL,
    actor     text NOT NULL,
    device_id uuid,
    action    text NOT NULL,
    detail    jsonb
);
CREATE INDEX IF NOT EXISTS audit_log_at ON audit_log (at DESC);
