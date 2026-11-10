-- When the downtime a "Release now" (or "Skip next downtime") ended early would
-- otherwise have ended, as the Mac last reported it. Portal-side only, never
-- sent to lulld: it tells the device page that a release is still in effect,
-- so "Reset overrides" can be offered.
ALTER TABLE device_settings ADD COLUMN released_until timestamptz;
