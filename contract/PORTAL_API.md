# Device API

The contract between the portal and lulld, the daemon in the
[Lull repository](https://github.com/nicholas-99w/lull). lulld fetches settings
every `Portal.PollIntervalSeconds`, checks in after each fetch and on every
phase change, and retries failures after 60 s.

Terms used below:

- **School nights** are windows starting Sunday to Thursday, and **weekend**
  windows start Friday or Saturday. Parents can opt out of each, follow the
  school's schedule, or customise the times.
- **School hours** are set by IT. The portal refuses any parent change that
  would touch them.
- **Release now** ends the downtime in progress. **Skip next downtime** is a
  release at the exact start of a window.
- **Reset overrides** clears any lock-down, release or skip. Parents see it as
  **End lock-down**, **Cancel unlock** or **Cancel skip**.

## Changing the API

This repository owns the device API: this document and the JSON fixtures in
[`fixtures/`](fixtures/). The portal's contract tests read them here; the Mac
repository keeps a copy in `Tests/Fixtures/portal/` that its Swift tests
decode. So that Macs already deployed keep working:

- **Within `/v1`, only add**: new optional fields, which old Macs ignore, and
  new endpoints. Never rename, remove or change the meaning or type of a field.
- **Anything else is `/v2`**, served alongside `/v1` until every Mac has moved.
- **Change the fixtures here first**, in the same PR as the portal change, then
  copy them to the Mac repository's `Tests/Fixtures/portal/` with its change.
  The files must stay identical in both places.
- Some fixtures are only decoded by the Mac's tests, for cases the portal never
  sends: `settings-opted-out.json` (the older `enabled: false` opt-out, with an
  unknown field) and `settings-invalid.json` (a bad time, which lulld must
  refuse). Keep them.

## How the profile and the portal combine

Portal management is on only when the Jamf profile has a valid `Portal.URL`
([Configuration guide](https://github.com/nicholas-99w/lull/blob/main/Docs/CONFIGURATION.md#parent-portal)). Then:

| Setting | Comes from |
|---|---|
| Master switch | Profile `Enabled` **and** portal `enabled`. The profile's `Enabled = false` always wins. (The portal now opts out per kind and always sends `enabled: true`; `false` is the older all-or-nothing opt-out) |
| Downtime windows | The profile's `Schedules` (IT's baseline), then per kind: **opted out** (`schoolNightsOff` / `weekendOff`) removes every profile window starting on its days (Sun–Thu, or Fri and Sat); **customised** replaces them with the parent's window; otherwise the kind **follows the profile**. A rule spanning both kinds (e.g. Mon–Fri) keeps the other kind's days. School-wide `DateExceptions` (e.g. an `AllDay` device-free day) still apply. Before the Mac first reaches the portal, the profile applies as is |
| Release now | Portal `releasedAt`: the downtime window in progress at that instant (scheduled, lock-down, or a holiday `AllDay`, as one merged run) ends there. Later windows are untouched; a release that arrives after the window ended has no effect. A `releasedAt` at the exact start of a window removes it (the portal's "Skip next downtime") |
| Lock down now | Portal `lockFrom`-`lockUntil`, on top of the windows. Applies even when the parent opted out of the schedules and on holiday `Skip` dates; never when the profile has `Enabled = false`. No warning period. At most 24 h (lulld refuses longer) |
| Advanced schedule | Portal `week`: replaces the profile's `Schedules` and the per-kind choices above entirely, with the parent's windows (named "Downtime"); empty means no downtime. School-wide `DateExceptions` still apply |
| School holidays | Portal `holiday` / `holidayOff` / `holidayWeek` over the portal's `holidayPeriods` (below): during a period the parent's holiday choice replaces the usual windows (school-night/weekend, the profile's `Schedules`, or `week`) (**opted out**: none; **customised**: the parent's window; **advanced**: the parent's `holidayWeek` windows; named "School holidays"). Without a holiday choice nothing changes. The profile's `DateExceptions` still apply first. Lock down now and release are unaffected |
| Holidays (`DateExceptions`), warnings, override, time, appearance, texts | Profile only |

**Enforcement never waits for the portal.** lulld caches the last valid
settings in `/var/db/lull/state.json` (root, 0600) and uses them while offline,
when the portal is blocked, or when TLS fails (for example because the clock was
changed). A student can't make a Mac forget its settings by cutting the network.

Settings that fail validation are refused and logged (`portal_settings_rejected`),
and the cached copy stays in force. Each change on the portal gets a higher
`revision`, and lulld refuses an older revision than the one it has (a stale or
replayed response), or the same revision with different content.

## Device identity

1. A Jamf policy script writes the site **enrolment secret** to
   `/var/db/lull/portal-enroll.json` (root, 0600). It never goes in the
   profile, because profiles are readable by local users.
2. With no device token, lulld calls **register** with the Mac's serial number
   and hardware UUID, and stores the returned token in `/var/db/lull/portal.json`
   (root, 0600). The portal stores only a hash of the token.
3. A serial that is already registered **with the same hardware UUID** is the
   same Mac without its token (it was reinstalled or re-imaged, or lost
   `portal.json`). It registers again by itself and **starts afresh**, as after
   a de-registration: school defaults, no lock-down, and the primary user is
   pinned again from the next check-in (audited as `re_registered_same_hardware`).
4. A serial that is already registered with a **different** hardware UUID is
   refused until IT clicks **Reset device** in the portal, so a leaked enrolment
   secret and a serial (printed on the case) aren't enough to take over another
   Mac's record. A reset lets the serial register once more from any hardware
   (after a logic board replacement, say) and keeps the settings and primary
   user. IT can also reset a Mac before reinstalling it to keep them.

Neither the secret nor the token is ever logged.

**De-registering.** IT can de-register a Mac in the portal (with a
confirmation). The portal keeps a tombstone so the Mac's token gets **410
Gone** (a 401 would make lulld simply register again). On a 410, lulld:

- deletes its token (`portal.json`) **and the enrolment secret** (`portal-enroll.json`),
- drops its cached portal settings, including any lock-down, so the
  profile (plist) settings apply again,
- shows `de-registered by IT` in `lullctl status` (not an error, so no EA
  warning), logs `portal_deregistered`, and stops contacting the portal.

To manage it remotely again, run the enrolment policy on the Mac again. It
then registers as a fresh device: school defaults, and the primary user is
pinned again from the next check-in.

## Which parent sees which Mac

Each check-in lists the GUI users on the Mac. The portal **pins the first
non-exempt user** it sees as the device's primary user; IT can change it. A
parent sees the Macs whose primary user is one of the **student usernames IT
linked to their account** (in the portal's Users area, or by CSV import).
Because the primary user is pinned, a student who logs into a classmate's Mac
doesn't move it into their own parent's view.

The Mac's local short user name must match the student username IT enters
for the parent (as it does with Jamf Connect or Platform SSO account naming).

Each user also has an optional `fullName`: the account's full name on the Mac
(its display name, `pw_gecos`, e.g. "Jane Smith"; omitted when empty). The
portal keeps the primary user's from the latest check-in that has it and shows
it in place of the username (IT sees both). It's for display only: matching is
always by username, and a student who renames their account changes only the
name shown.

## Endpoints

All paths are relative to `Portal.URL`, https only, JSON with ISO 8601 dates.
Unknown fields must be ignored by both sides, so either can add fields
without a new API version.

### `POST api/device/v1/register`

Request ([fixture](fixtures/register-request.json)):

```json
{ "serialNumber": "C02EXAMPLE01", "hardwareUUID": "6B29FC40-…", "enrolmentSecret": "…", "version": "0.2.0" }
```

`200` returns `{ "deviceId": "…", "deviceToken": "…" }`
([fixture](fixtures/register-response.json)). `401` means a
wrong secret, and `409` means the serial is already registered to a different
hardware UUID (IT must reset it). The same serial and hardware UUID get `200`
and start afresh (see "Device identity").

### `GET api/device/v1/settings`

`Authorization: Bearer <deviceToken>`, with `If-None-Match` set to the last ETag.
`200` returns the settings ([fixture](fixtures/settings.json)),
`304` means no change, `401` means the token is no longer valid (lulld
registers again), and `410` means IT de-registered the Mac (see above).

```json
{ "revision": 1760000000000, "enabled": true,
  "schoolNights": { "start": "21:30", "end": "06:30" },
  "weekend":      { "start": "23:00", "end": "08:00" },
  "lockFrom": "2026-10-17T02:00:00Z", "lockUntil": "2026-10-17T04:00:00Z" }
```

- `schoolNights` / `weekend`: optional. **Absent = follow the school's
  (profile's) schedule** for those days ([fixture](fixtures/settings-follow.json));
  present = the parent's own window. New Macs follow the school.
- `schoolNightsOff` / `weekendOff`: optional, `true` = the parent opted out
  of those days' schedules ([fixture](fixtures/settings-optout.json));
  that kind's window is then omitted. Older lulld builds ignore the flags and
  keep following the school's schedule for those days (they fail closed).
- `revision`: positive and only increasing. The portal sets it to
  `max(previous + 1, unix milliseconds)`, so it keeps increasing after a
  database restore.
- Times are `HH:mm` in the profile's `Timezone`. If `end` is at or before
  `start`, the window ends the next day. `start` must differ from `end`.
- `lockFrom`/`lockUntil` (optional, both or neither, [fixture](fixtures/settings-lock.json)):
  a "Lock down now", as UTC instants in **whole seconds** with `Z` (lulld's
  ISO 8601 parser rejects fractional seconds). `lockUntil` must be after
  `lockFrom` and at most 24 h later. A lock-down in the past has no effect;
  ending one early moves `lockUntil` to that moment. lulld picks it up at its
  next poll, so lower `Portal.PollIntervalSeconds` if lock-downs should start sooner.
- `releasedAt` (optional, [fixture](fixtures/settings-release.json)):
  a "Release now", same format. A running lock-down is left as it is: the
  release cuts the window containing it, so it ends there too, along with any
  scheduled downtime it merged with.
  It may be in the future: "Skip next downtime" sets it to the exact start of
  the window the Mac reported as starting soon, which cuts that window to
  nothing. A new lock-down clears it, as does "Reset overrides" (which also
  clears `lockFrom`/`lockUntil`), and so does saving different schedule times
  (a skip or release of the old windows would cut or skip the wrong one).
- `holiday` (optional, same shape and rules as `schoolNights`): the parent's
  times for school holidays ([fixture](fixtures/settings-holiday.json)).
  `holidayOff` (optional, `true`): opted out of downtime during school
  holidays; `holiday` is then omitted ([fixture](fixtures/settings-holiday-off.json)).
  Neither = the usual settings through the holidays.
- `holidayPeriods` (optional, sent only with `holiday`, `holidayOff` or `holidayWeek`): when
  the holiday choice applies, as
  `{ "start": "2026-10-16T15:30", "lastDay": "2026-10-25" }` in the profile's
  `Timezone`: `start` is a local date and time (`YYYY-MM-DDTHH:mm`), the end of
  school hours on the last school day; `lastDay` (`YYYY-MM-DD`, after the
  start's date) is the day before school goes back. Periods are in date order,
  each starting after the previous `lastDay`. The portal works them out from
  IT's school holidays and school hours; weekends next to a holiday count as
  part of it. Windows belong to the day they **start**, and lulld applies a
  period like this:

  | Day | Usual windows | Holiday windows |
  |---|---|---|
  | The start's date | Kept if they start before `start`, dropped from then on | Start no earlier than `start`; dropped if they end by then |
  | Days in between | Dropped | As set |
  | `lastDay` | Kept (normally that night's school-night window) | Cut where the first usual window that day starts, or at midnight if there's none; dropped if they start at or after that point |

  A bad date, time or order rejects the settings, as a bad time does. The list
  isn't trimmed as days pass: it changes only when IT adds or removes a holiday
  or changes when school ends, and then every Mac with a holiday choice gets a
  new `revision`. Older lulld builds ignore all three fields and keep their
  usual settings through the holidays.
- `week` (optional, [fixture](fixtures/settings-week.json)): an advanced
  schedule, the parent's hour-by-hour grid as windows
  `{ "day": "Mon", "start": "21:00", "end": "07:00" }`. `day` is the day the
  window **starts** (`Mon` … `Sun`, as the profile spells them); times as
  above, except that `start` equal to `end` is a whole 24 hours (only ever
  `00:00`–`00:00`). When present (even empty, which is no downtime) it
  replaces the profile's `Schedules` and the school-night and weekend choices.
  The portal works the windows out: each run of downtime round the week is
  one window, a run of 24 hours or more is split at each midnight, and
  **school hours are always left out** (an hour that is partly school hours,
  such as 3–4 pm with school until 3:30 pm, gives a window from 3:30 pm). So
  `week` changes when IT changes school hours, with a new `revision`. Windows
  are in week order, Monday first, and don't overlap. The portal still sends
  the school-night and weekend fields next to `week`, as the parent last set
  them in the simple view: older lulld builds ignore `week` and apply those
  instead.
- `holidayWeek` (optional, [fixture](fixtures/settings-holiday-week.json)):
  the same for school holidays, with any hours (school hours don't apply).
  Sent only with `holidayPeriods`, which it then goes with as `holiday` does;
  when present it is the holiday choice, and `holiday` / `holidayOff` (sent as
  the parent last set them, for older lulld builds) mean nothing.
- A Mac with no settings saved yet gets the portal's school defaults, chosen by IT.

### `POST api/device/v1/checkin`

`Authorization: Bearer <deviceToken>`. Sent on every poll (`Portal.PollIntervalSeconds`)
and as soon as the phase changes. The body
([fixture](fixtures/checkin.json)) is what the Mac is enforcing:
phase, current or next window, effective `enabled` and schedules, applied
`settingsRevision` (null while the profile's schedules apply), `settingsError`,
config validity, trusted-time status and GUI users (with their full names), plus `profileSchedules`:
the profile's own (school) schedules, so the portal can show what "follow the
school" means for this Mac and pre-fill *Customise*. Times are lulld's trusted
times. It contains no secrets. `204` means the check-in was accepted; `401`
and `410` as for settings.

The portal shows **Applied** when the reported `settingsRevision` equals the
current revision, and **Pending** otherwise. "Last check-in" is the time the
portal received the check-in.

Until the Mac checks in again, its last check-in may be out of date: while a
change is **Pending** (unless the Mac refused it, with `settingsError`), and
once `nextTransition` is more than 10 minutes past (**Waiting for check-in**;
lulld checks in as soon as its phase changes). Parents and IT then get only
Reset overrides (labelled for what it ends), until the Mac checks in. A Mac that hasn't checked in for
20 minutes shows as **Offline?**.
