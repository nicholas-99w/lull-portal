-- When scheduled downtime that a "Lock down now" runs into starts, as the Mac
-- reported it when the lock-down was saved. Portal-side only, never sent to
-- lulld: once the Mac applies the lock-down it reports the two as one window,
-- and before this time "Release now" ends just the lock-down.
ALTER TABLE device_settings ADD COLUMN lock_joins_at timestamptz;
