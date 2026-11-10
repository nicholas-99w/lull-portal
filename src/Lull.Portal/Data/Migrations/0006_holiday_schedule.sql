-- A parent's choice for school holidays, next to school nights and the
-- weekend: their own times (start/end), opted out, or neither (the Mac keeps
-- its usual schedules). Sent to lulld with the holiday days worked out from
-- IT's holidays; it only takes effect on those days.
ALTER TABLE device_settings ADD COLUMN holiday_start time;
ALTER TABLE device_settings ADD COLUMN holiday_end time;
ALTER TABLE device_settings ADD COLUMN holiday_off boolean NOT NULL DEFAULT false;
