-- Two-step sign-in (TOTP, an authenticator app's 6-digit codes), required for
-- local admin accounts. The key is encrypted with the portal's Data Protection
-- keys (the /keys volume), so a database dump alone can't produce codes.
-- totp_last_step is the 30-second step of the last code used: each code works once.
ALTER TABLE users ADD COLUMN totp_secret text;
ALTER TABLE users ADD COLUMN totp_last_step bigint;
ALTER TABLE users ADD COLUMN totp_enabled_at timestamptz;
-- Local admins are signed out, so each sets up two-step sign-in at their next
-- sign-in (a session from before this has none). Entra admins use Entra's own MFA.
UPDATE users SET security_stamp = gen_random_uuid() WHERE role = 'Admin' AND entra_object_id IS NULL;
