-- A parent's advanced schedules: downtime hour by hour across the week, as
-- 168 '0'/'1' cells from Monday midnight (Domain/WeekGrid.cs). week replaces
-- the school-night and weekend choices, which are kept for going back to them
-- (and for Macs too old for it); holiday_week likewise replaces the holiday
-- choice. NULL = the simple choices apply.
ALTER TABLE device_settings ADD COLUMN week text CHECK (week ~ '^[01]{168}$');
ALTER TABLE device_settings ADD COLUMN holiday_week text CHECK (holiday_week ~ '^[01]{168}$');
