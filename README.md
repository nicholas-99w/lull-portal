# Lull portal

A self-hosted web portal for schools that manage student Macs with
[Lull](https://github.com/nicholas-99w/lull). Parents manage downtime on their
child's Mac from a phone or computer. IT manages accounts, sees every Mac and
sets the school hours that parents can't change.

Built with ASP.NET Core and PostgreSQL, and deployed with Docker Compose.

## Features

**For parents**

- See what each child's Mac is doing now and what happens next
- Lock a Mac down, unlock it, or skip the next downtime
- Follow the school's schedule, or set their own for school nights, weekends
  and school holidays

**For IT**

- Create parent and admin accounts one at a time or by CSV import
- See every Mac and its status, and which ones need attention
- Set school hours, school holidays and limits on lock-downs
- Add the school's name, logo and colours
- Review every change in an audit log

**Security and operations**

- Sign in with local accounts, Microsoft Entra ID, or both
- Two-step sign-in for local admin accounts
- Hardened containers and a database user with least privilege
- Automatic nightly database backups and a one-command update

## How it works

Each Mac runs Lull, which registers with the portal and regularly fetches its
settings over HTTPS. The school's Jamf profile provides the baseline schedule,
and parents' choices apply on top of it within the limits IT sets. If a Mac
can't reach the portal, it keeps enforcing its last settings.

## Try it

You need Docker with Compose v2.

```sh
git clone https://github.com/nicholas-99w/lull-portal.git
cd lull-portal
cp .env.example .env
```

Replace the three `change-me` secrets in `.env`, then start the portal. This
pulls the latest release's image:

```sh
docker compose up -d --wait
```

Open <http://localhost:8080> in Chrome, Edge or Firefox and create the owner
account. Safari won't keep you signed in on `http://localhost` unless you set
`LULL_TRUST_FORWARDED_HEADERS=false` in `.env`.

To run it for real, follow the [administration guide](docs/administration.md).

## Documentation

| Guide | Covers |
|---|---|
| [Administration](docs/administration.md) | Deploying, accounts, updates, backups and troubleshooting |
| [Sign in with Entra ID](docs/entra-id.md) | Optional Microsoft sign-in |
| [`.env.example`](.env.example) | Every setting, with its default |
| [Device API](contract/PORTAL_API.md) | The contract between the portal and Lull on the Mac |
| [Changelog](CHANGELOG.md) | What changed in each release |
| [Security policy](SECURITY.md) | Reporting a vulnerability, and which versions get fixes |
| [Contributing](CONTRIBUTING.md) | Issues, pull requests and working on the code |

## Versions

From 1.0.0 the portal follows [semantic versioning](https://semver.org):

| Release | What it can contain |
|---|---|
| Patch (1.0.1) | Fixes only |
| Minor (1.1.0) | New features, and database migrations that `./update.sh` applies |
| Major (2.0.0) | Changes you must act on, such as a renamed or removed setting in `.env` |

- **Macs keep working across 1.x.** The device API (`/api/device/v1`) only
  gains optional fields and new endpoints. A breaking change would be a new
  `/v2`, served alongside `/v1` until every Mac has moved.
- **Update in order, never back.** `./update.sh` goes from any 0.9 or 1.x
  release to the latest. Going back to an older version needs a backup taken
  before the update (docs/administration.md, "Update").
- **Only the latest release gets fixes**, and its image is rebuilt every week
  on the base images' security patches ([SECURITY.md](SECURITY.md)).

## Development

You need the .NET 10 SDK and Docker. `./test.sh` runs every test against a
throwaway Postgres container. CI also checks formatting, so run this before
pushing:

```sh
dotnet format Lull.Portal.slnx
```

- **Database changes:** add the next numbered script to
  `src/Lull.Portal/Data/Migrations/`, and never edit one that has been
  released.
- **Device API changes:** follow
  [Changing the API](contract/PORTAL_API.md#changing-the-api), because Macs
  already deployed must keep working.
- **Running your changes in Docker:** build them under another image name, so
  the release's name keeps meaning the published image:
  `LULL_PORTAL_IMAGE=lull-portal:dev docker compose up -d --build --wait`.
- **Releases:** in a pull request, add a section for the version to the
  changelog and set the same version in `compose.yml`'s `x-portal-image` (CI
  checks they match). After it merges, tag that commit on `main` as `vX.Y.Z`
  and publish a GitHub release with those notes. Publishing the release
  pushes its image to `ghcr.io/nicholas-99w/lull-portal`, and `./update.sh`
  deploys the newest tag.

## License

[MIT](LICENSE)
