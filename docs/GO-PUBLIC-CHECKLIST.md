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

## Receipts — Validation end-to-end

Avant la v1, valider la chaine complete avec un VRAI receipt (pas des fakes) :

- [ ] Generer une paire de cles Ed25519
- [ ] Creer un DID Document avec la cle publique
- [ ] Publier le DID Document sur un domaine (/.well-known/did.json)
- [ ] Signer un receipt JWT avec la cle privee
- [ ] Soumettre le receipt a l'API et verifier : rating accepte avec poids 1.0
- [ ] Tester les cas d'erreur reels : cle invalide, nonce replay, timestamp expire
- [ ] Ajouter des tests unitaires pour le DID resolver (parsing de vrais DID Documents)

## Distribution

- [ ] Publier le MCP server sur npm (@trustscoreagent/mcp-server)
- [ ] Publier le serveur MCP sur smithery.ai
- [ ] Publier sur mcp.run
- [ ] PR dans LangChain (tool TrustScoreAgent)
- [ ] PR dans CrewAI
- [ ] Premier post (Hacker News, Reddit, Dev.to)
- [ ] Configurer l'email hello@trustscoreagent.com (Cloudflare Email Routing)

## Securite

- [ ] Audit des secrets : aucun mot de passe/token dans l'historique git
- [ ] Verifier que les endpoints de production repondent correctement
- [ ] Tester le rate limiting en production
