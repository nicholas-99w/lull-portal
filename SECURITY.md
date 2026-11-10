# Security policy

## Reporting a vulnerability

Please don't open a public issue. Report it privately through GitHub instead:
on this repository's **Security** tab, choose **Report a vulnerability**. Only
the maintainers see the report.

Include what you found, how to reproduce it, and the version you tested
(`docker compose images portal` shows the image tag). You'll get a reply
within a week. A fix goes out as a patch release, with credit in the changelog unless
you'd rather not be named.

## Supported versions

Only the latest release gets security fixes. Its image is also rebuilt every
week on the .NET base images' latest patches, so keep servers current with
`./update.sh` (docs/administration.md, "Update").

## Scope

In scope: the portal (this repository), its Docker image, `compose.yml` and
the scripts that ship with it.

Out of scope: the Lull app on the Mac (report those to its own repository),
and problems that need an attacker to already control the server, its `.env`
or the database.
