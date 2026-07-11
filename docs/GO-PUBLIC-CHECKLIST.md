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

- [ ] Publier le MCP server sur npm (@trustscoreagent/mcp-server)
- [ ] Publier le serveur MCP sur smithery.ai
- [ ] Publier sur mcp.run
- [ ] Deployer la landing page sur Cloudflare Pages (apex trustscoreagent.com, dossier site/)
- [ ] PR dans le repo modelcontextprotocol/servers
- [ ] PR dans LangChain (tool TrustScoreAgent)
- [ ] PR dans CrewAI
- [ ] Premier post (Hacker News, Reddit, Dev.to)
- [ ] Configurer les emails security@ et hello@trustscoreagent.com (Cloudflare Email Routing) — security@ est le contact de SECURITY.md

## Receipts — Validation end-to-end

Avant la v1, valider la chaine complete avec un VRAI receipt (pas des fakes) :

- [ ] Generer une paire de cles Ed25519
- [ ] Creer un DID Document avec la cle publique
- [ ] Publier le DID Document sur un domaine (/.well-known/did.json)
- [ ] Signer un receipt JWT avec la cle privee
- [ ] Soumettre le receipt a l'API et verifier : rating accepte avec poids 1.0
- [ ] Tester les cas d'erreur reels : cle invalide, nonce replay, timestamp expire
- [ ] Ajouter des tests unitaires pour le DID resolver (parsing de vrais DID Documents)

## Infrastructure post-deploy

- [ ] Configurer AdminApiKey dans GCP Secret Manager
- [ ] Mettre a jour l'URL par defaut du MCP server (staging → production)
- [ ] Mettre a jour llms.txt et agent.json avec les URLs de production
- [ ] Lancer infra/setup-scheduler.sh (Cloud Run Job + Cloud Scheduler)
      → EigenTrust + Merkle anchoring toutes les heures automatiquement
      → Ne pas oublier : necessite l'image Docker deployee en prod d'abord
- [ ] Verifier que le job tourne : gcloud run jobs execute trustscoreagent-hourly --region europe-west1
- [ ] Configurer un wallet Base L2 pour le blockchain anchoring (Phase 2)

## Securite

- [ ] Audit des secrets : aucun mot de passe/token dans l'historique git
- [ ] Verifier que les endpoints de production repondent correctement
- [ ] Tester le rate limiting en production
