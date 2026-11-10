# Contributing

Thanks for your interest. Lull's portal is maintained by a small team, so:

- **Bugs and questions:** open an issue using one of the templates. For a
  security problem, follow [SECURITY.md](SECURITY.md) instead.
- **Changes:** open an issue first to agree on the change, before writing a
  pull request. Unasked-for pull requests may be closed.

## Working on the code

See the README, "Development": you need the .NET 10 SDK and Docker.

- `./test.sh` runs every test against a throwaway Postgres container.
- Run `dotnet format Lull.Portal.slnx` before pushing. CI checks formatting.
- Database changes: add the next numbered script to
  `src/Lull.Portal/Data/Migrations/`. Never edit one that has been released.
- Device API changes must keep deployed Macs working: follow
  [Changing the API](contract/PORTAL_API.md#changing-the-api).

By contributing, you agree that your contribution is licensed under the
[MIT License](LICENSE).
