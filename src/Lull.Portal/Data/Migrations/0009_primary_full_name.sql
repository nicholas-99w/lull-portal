-- The primary user's full name on the Mac (the local account's display name,
-- e.g. "Jane Smith"), from the check-ins that list that user. For showing
-- only: parents are still linked by primary_username. NULL = not known yet
-- (an older lulld, or the user hasn't been on a check-in since being pinned).
ALTER TABLE devices ADD COLUMN primary_full_name text;
