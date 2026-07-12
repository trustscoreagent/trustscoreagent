# Contributing to TrustScoreAgent

Thanks for your interest in improving TrustScoreAgent — a free, open reputation
registry for AI microservices. Contributions of all kinds are welcome: bug reports,
documentation, new framework integrations, and code.

By participating you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to contribute

- **Report a bug** — open an issue with the *Bug report* template.
- **Request a feature** — open an issue with the *Feature request* template.
- **Report a vulnerability** — do **not** open a public issue; follow
  [SECURITY.md](SECURITY.md).
- **Improve docs** — everything under `docs/` and the READMEs is fair game.
- **Add an integration** — MCP clients, framework tools (LangChain, CrewAI, …),
  SDKs. These are especially valued: the registry is only useful if agents can reach it.

## Development setup

Prerequisites: **.NET 8 SDK**, **Docker** (for PostgreSQL + Redis), and **Node.js 20+**
(for the MCP server).

```bash
# 1. Start PostgreSQL and Redis
docker compose up -d

# 2. Run the API (migrations run automatically on startup via DbUp)
dotnet run --project src/TrustScore.Api

# 3. Explore the API
open http://localhost:5000/swagger

# 4. Run the test suite
dotnet test
```

MCP server:

```bash
cd mcp-server
npm install
npm run build      # compile TypeScript to dist/
npm run dev        # run from source with tsx
```

## Before you open a pull request

Run these locally — CI enforces all of them and a red check blocks the merge:

```bash
dotnet build --warnaserror        # no warnings
dotnet format --verify-no-changes # formatting
dotnet test                       # all tests green
```

For MCP server changes: `cd mcp-server && npm run build` must succeed.

New behavior should come with tests. We use **xUnit** with **FluentAssertions**
(`[Fact]` + `.Should()`); integration tests run against a real PostgreSQL container.

## Coding conventions

These mirror the existing codebase — match the surrounding style:

- **Minimal API** style (no MVC controllers).
- **Dapper** with raw SQL — no Entity Framework.
- **Redis is a cache, never a hard dependency**: the API must keep working (falling
  back to PostgreSQL) if Redis is down.
- All HTTP responses are JSON with **`snake_case`** field names.
- Services are identified by `?service=` (URL, domain, or DID) and normalized to a
  canonical domain via `ServiceIdentifier.Normalize()`.
- Unknown services return a neutral score (`0.5`, `known: false`) — never a 404.

## Commits and branches

- Branch off `main`; use a descriptive name (`fix/…`, `feat/…`, `docs/…`).
- Use **[Conventional Commits](https://www.conventionalcommits.org/)** for messages
  (`feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `test:`).
- Feature branches are merged with `--no-ff` after review and green CI.
- Keep PRs focused; fill in the pull-request template (what / why / how / testing).

## Developer Certificate of Origin (DCO)

We use the [DCO](https://developercertificate.org/) instead of a CLA. It's a simple
statement that you wrote the patch or otherwise have the right to submit it under the
project's license. Certify it by signing off each commit:

```bash
git commit -s -m "feat: add CrewAI tool"
```

This appends a `Signed-off-by: Your Name <you@example.com>` line. By signing off you
agree to the DCO. Contributions are licensed under **Apache-2.0** (see [LICENSE](LICENSE)).

## Project status

TrustScoreAgent is **Phase 1 (early)** — see the trust model in
[SECURITY.md](SECURITY.md) and the roadmap notes in [README.md](README.md). If you're
planning a larger change, open an issue first so we can align on direction.
