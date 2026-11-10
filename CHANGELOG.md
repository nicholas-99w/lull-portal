# Changelog

Each release's notes say how to update, whether it has database migrations,
and whether anything in `.env` or on the Macs needs to change. Versions follow
the README, "Versions".

## 1.0.0 (10 November 2026): First public release

The first public release of the Lull portal, a self-hosted web portal where
parents manage downtime on their child's Mac and IT manages the school's
accounts, Macs and limits. To install it, follow the
[administration guide](docs/administration.md).

- **For parents:** see what each Mac is doing now and what happens next. Lock
  a Mac down, unlock it, or skip the next downtime. Follow the school's
  schedule, or set your own for school nights, weekends and school holidays,
  simply or hour by hour. Pages keep up by themselves while they're open.
- **For IT:** create parent and admin accounts one at a time or by CSV import.
  See every Mac, its status and which ones need attention. Set school hours,
  school holidays and limits on lock-downs, add the school's name, logo and
  colours, and review every change in the audit log.
- **Sign-in:** local accounts, Microsoft Entra ID, or both. Local admins use
  two-step sign-in, and accounts lock for 15 minutes after 5 wrong attempts.
- **Operations:** one `docker compose up` from a published image for amd64 and
  arm64, hardened containers, a least-privilege database user, nightly
  backups, and `./update.sh` to update. The latest release's image is rebuilt
  every week on the base images' security patches.
- **Macs:** the device API is `/api/device/v1`, and stays compatible across
  1.x ([contract/PORTAL_API.md](contract/PORTAL_API.md)). Use the latest
  [Lull](https://github.com/nicholas-99w/lull) release on the Macs. Older
  Macs keep working, without the features that need a newer Lull.

**Coming from a 0.9 release?** The portal has moved to a new repository with
a fresh history, at the same address. There are no database migrations,
nobody is signed out, and no `.env` changes are needed. Because the history is
new, `./update.sh` can't make this move by itself. Run these once in the
portal's folder, then use `./update.sh` as usual:

```sh
git fetch --tags origin
git -c advice.detachedHead=false checkout v1.0.0
./update.sh
```
