-- Accounts that sign in with Entra ID (Microsoft) instead of a password: their
-- Entra object ID (the token's oid claim), which stays the same when their
-- email or name changes. NULL for a local account (password and setup code).
-- An Entra account's email, name, role and students come from its token at
-- each sign-in.
ALTER TABLE users ADD COLUMN entra_object_id uuid UNIQUE;
