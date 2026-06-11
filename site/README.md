# Landing page

A single self-contained `index.html` for `trustscoreagent.com` — no build step, no
framework, no external requests (system fonts, inline CSS, inline SVG favicon). Minimalist,
dark, terminal-styled. Includes meta/Open Graph tags, JSON-LD structured data, and
`<link rel="alternate">` to `llms.txt` and `agent.json` so both humans and agents parse it.

## Deploy

Any static host works. With Cloudflare Pages (matches the project's stack):

```bash
# Project settings → Build output directory: site/  (no build command)
# Or via Wrangler:
npx wrangler pages deploy site --project-name trustscoreagent
```

Then point the apex `trustscoreagent.com` at the Pages project (the API stays on
`api.trustscoreagent.com`).

## Probe identity

`site/probe/did.json` is served at `https://trustscoreagent.com/probe/did.json`, which is
where the seed probe's DID `did:web:trustscoreagent.com:probe` resolves. It deploys with this
site — no extra subdomain or DNS record needed. The document carries no signing key (the probe
only rates, it never signs receipts); it just identifies the probe and links to the source.
