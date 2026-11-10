-- When a Mac first tried to register with this serial while it was already
-- registered (409), since the serial last registered successfully. lulld
-- retries at every poll, so only that first refusal is audited and logged;
-- IT's device page shows it as a hint to reset the Mac.
ALTER TABLE devices ADD COLUMN register_refused_at timestamptz;
