# Administration guide

How to deploy and look after a Lull portal. You'll need a server with Docker
and Compose v2, a hostname for the portal, and an HTTPS reverse proxy with a
certificate your Macs trust.

## Deploy

1. **Clone the repository onto the server.** Keep this folder: updates run
   from it.

   ```sh
   git clone https://github.com/nicholas-99w/lull-portal.git
   cd lull-portal
   cp .env.example .env
   ```

2. **Edit `.env`.** Set `POSTGRES_PASSWORD`, `LULL_APP_DB_PASSWORD` and
   `LULL_ENROLMENT_SECRET` (at least 16 characters), and set
   `LULL_ALLOWED_HOSTS` to the portal's hostname. Every other setting is
   optional and described in [`.env.example`](../.env.example).

3. **Set up the reverse proxy.** Forward HTTPS to `127.0.0.1:8080` and pass on
   `X-Forwarded-For` and `X-Forwarded-Proto`. Without the client's real
   address, every parent shares one sign-in rate limit.

4. **Start the portal**, then go straight to the next step:

   ```sh
   docker compose up -d --wait
   ```

   This pulls the portal's image for the latest release from
   `ghcr.io/nicholas-99w/lull-portal`, so the server doesn't build anything.

5. **Open the portal and create the owner account.** Until an owner exists,
   whoever opens the portal first can create it, so do this before anyone
   else can reach it.

6. **Connect your Macs.** Follow
   [Parent portal](https://github.com/nicholas-99w/lull/blob/main/Docs/DEPLOYMENT.md#parent-portal-optional)
   in Lull's deployment guide.

Optionally, set up [Sign in with Entra ID](entra-id.md).

**If the proxy is on another machine,** set `LULL_PORTAL_BIND=0.0.0.0` and
`LULL_TRUSTED_PROXIES` to the proxy's address, and firewall port 8080 so that
only the proxy can reach it.

**Run a single portal container.** Sign-in keys and rate limits belong to the
one instance, so a load-balanced copy would sign people out.

### Firewalls that decrypt HTTPS

If your network uses SSL/TLS inspection, bypass decryption for these hosts
(HTTPS, port 443). Otherwise pulls fail with a certificate error.

| Needed for | Hosts |
|---|---|
| Portal image | `ghcr.io`, `pkg-containers.githubusercontent.com` |
| Postgres image | `registry-1.docker.io`, `auth.docker.io`, `production.cloudflare.docker.com` |
| Building the portal yourself (`./update.sh --main`) | `mcr.microsoft.com`, `*.data.mcr.microsoft.com`, `api.nuget.org`, `*.nuget.org` |
| Entra ID sign-in, if used | `login.microsoftonline.com` |
| Macs reaching the portal through the firewall | The portal's hostname |

## Accounts

| Role | Can do |
|---|---|
| **Parent** | Manage the Macs of the students linked to their account |
| **Admin** | Everything in the portal except changing the owner account or the owner-only sign-in settings |
| **Owner** | The first admin. Always keeps password sign-in, so IT can get in if Microsoft sign-in breaks, and can't be deleted |

Create people under **Users**, one at a time or with **Import CSV** (the page
shows the format). Each new local account gets a one-time setup code, valid
for 30 days, which the person enters on **Set up your password**.

The portal doesn't send email, so you hand out the setup codes yourself. After
a CSV import you can download them for a mail merge, but only once.

### Linking parents to Macs

The first user seen on a Mac, other than users exempt from Lull, is pinned as
its primary user. A parent sees the Macs whose primary user is one of the
student usernames on their account, so these must match the short user names
on the Macs. IT can change a Mac's primary user on its page.

### Getting back in

| Problem | Fix |
|---|---|
| Forgotten password | IT issues a new setup code with **Reset password** |
| Account locked after 5 wrong attempts | Wait 15 minutes, or IT unlocks it under **Users** |
| Admin lost their authenticator | Another admin issues a new setup code, which also resets two-step sign-in |
| Owner locked out | On the server, run `docker compose exec portal dotnet Lull.Portal.dll --reset-owner` and use the code it prints |

## Update

Run this in the portal's folder on the server:

```sh
./update.sh
```

It backs up the database, deploys the latest release, applies any migrations
and restarts the portal, stopping at the first step that fails. Run it every
few months even without a new release. The latest release's image is rebuilt
every week on the base images' security patches, and the update pulls them.

Check the [changelog](../CHANGELOG.md) before updating. To test unreleased
code on a non-production server, use `./update.sh --main`. No image is
published for unreleased code, so this builds it on the server. Until the next
release, put `LULL_PORTAL_IMAGE=lull-portal:main` in front of other
`docker compose` commands in that folder, or they use the release's image.

The portal can't run on a database that a newer version has migrated, so don't
go back to an older version without also restoring a backup taken before the
update.

## Backups

The `backup` service backs up the database when it starts and every night at
02:00 Sydney time, into `backups/`. Backups are kept for 14 days. Change these
with the `LULL_BACKUP_*` settings.

- **Copy `backups/` off the server** with your normal server backups. The
  files contain password hashes, so protect them like the database.
- **Also back up the `portal_portal-keys` Docker volume.** It isn't in the
  database backups. Without it, everyone is signed out and admins' two-step
  codes stop working until each gets a new setup code.
- **To back up now:** `docker compose run --rm backup --now`
- **To check:** `docker compose ps` shows `backup` as unhealthy once the newest
  backup is more than 26 hours old.

To restore a backup, which replaces everything in the database:

```sh
docker compose stop portal
docker compose run --rm backup --restore /backups/lull-20261019-020000.dump
docker compose up -d --wait
```

On a new server, set up `.env`, run `docker compose up -d --wait` once,
copy the file into `backups/`, then restore it as above.

## Troubleshooting

Start with `docker compose ps` and `docker compose logs portal`. The error page
shows a reference that matches a `RequestId` in the portal's log.

| Symptom | Cause and fix |
|---|---|
| `password authentication failed for user "lull_app"` | Run `docker compose up -d`, which sets the password from `.env` again |
| `password authentication failed for user "lull"` | `POSTGRES_PASSWORD` changed after the database was created. Put the old value back, or set the database to the new one with `docker compose exec db psql -U lull -d lull -c '\password lull'`, then run `docker compose up -d --wait` |
| `The database was migrated by a newer portal` | An older version is running. Deploy that version or later |
| `The database needs migrating` | The migrate step didn't complete. Check `docker compose logs migrate` |
| Parents get "too many requests" | The proxy isn't passing on client addresses, or `LULL_TRUST_FORWARDED_HEADERS` is `false` |
| Many sign-in failures in the log | Failed sign-ins are logged as warnings in the `SignIn` category. A spike suggests someone is guessing passwords |
| A Mac is refused registration | It's already registered to different hardware. Use **Reset device** on its page |
