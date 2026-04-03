# Checklist — Passage en public

A faire quand le MVP est pret et qu'on repasse le repo en public.

## GitHub

- [ ] Repasser le repo en public (Settings → Danger Zone → Change visibility)
- [ ] Activer "Required reviewers" sur l'environnement `production` (disponible uniquement sur les repos publics en plan Free)
- [ ] Verifier que les secrets GitHub ne sont pas exposes dans les logs de CI
- [ ] Verifier que le `.gitignore` exclut bien `.env`, credentials, etc.

## Documentation

- [ ] README a jour avec des exemples fonctionnels
- [ ] llms.txt et agent.json pointent vers les bonnes URLs de production
- [ ] OpenAPI spec accessible publiquement

## Distribution

- [ ] Publier le serveur MCP sur smithery.ai
- [ ] PR dans LangChain (tool TrustScoreAgent)
- [ ] Premier post (Hacker News, Reddit, Dev.to)
- [ ] Configurer l'email hello@trustscoreagent.com (Cloudflare Email Routing)

## Securite

- [ ] Audit des secrets : aucun mot de passe/token dans l'historique git
- [ ] Verifier que les endpoints de production repondent correctement
- [ ] Tester le rate limiting en production
