# EigenTrust — Anti-Sybil Scoring (Phase 2)

> A implementer quand on atteint 50-100 agents actifs.
> Avant cela, le probleme Sybil n'existe pas et le calcul n'a pas assez de donnees.

## Principe

EigenTrust calcule un score de confiance (0.0-1.0) pour chaque agent evaluateur.
Les agents qui evaluent de maniere coherente avec le consensus ont un trust eleve.
Les agents Sybil (faux agents votant en clique) convergent vers un trust ~0.

## Algorithme

```
1. Construire la matrice de confiance C :
   c_ij = (nombre de ratings coherents de i) / (total ratings de i)

2. Normaliser chaque ligne de C

3. Iterer : t(k+1) = C^T * t(k)
   avec t(0) = seed raters

4. Convergence en O(log n) iterations

5. Le vecteur t donne le trust score de chaque agent
```

## Decisions

### Frequence
Recalcul **toutes les heures** via Cloud Run Job (meme codebase C#, flag `--eigentrust`).
Un Sybil a au maximum 1h d'impact avant detection.

### Seed raters
Les **10 agents avec le plus de ratings verifies** (receipts), recalcules dynamiquement.
Pas de liste manuelle. Si un seed rater se comporte mal, il sort du top 10 naturellement.

### Definition de "coherent"
On compare les **metriques objectives uniquement** (status_code, latency_ms, schema_valid),
PAS le quality_score subjectif. Les metriques objectives sont infalsifiables si le receipt
est verifie. Cela resiste au "consensus empoisonne" (Sybil qui gonflent le quality_score).

### Impact sur les scores

```
rating_weight = receipt_weight × agent_trust_score

Agent trust 0.9 + receipt verifie  → poids 0.9   (influence forte)
Agent trust 0.9 + sans receipt     → poids 0.27  (influence moderee)
Agent trust 0.05 (Sybil) + receipt → poids 0.05  (quasi ignore)
Agent trust 0.05 (Sybil) + sans   → poids 0.015 (ignore)
```

Plancher de trust a 0.1 : meme un Sybil detecte garde un poids residuel minimal.

### Trust par defaut (nouveaux agents)
**0.5** (neutre). Ratings comptent a moitie. Le score evolue des le prochain batch.
Compromis : un Sybil a 1h de ratings a poids 0.5 avant correction,
mais un vrai nouvel agent n'est pas bloque.

### Visibilite
Un agent peut consulter **son propre trust score** (`GET /v1/agent/trust?did={did}`),
mais PAS celui des autres. Transparence personnelle sans donner d'infos aux attaquants.

### Execution
Cloud Run Job + Cloud Scheduler (cron horaire). Meme image Docker, flag `--eigentrust`.
Charge le graphe depuis PostgreSQL, calcule, ecrit dans PostgreSQL + Redis.

### Stockage
Table `agents` existante, champ `trust_score` (defaut 0.5).
Cache Redis pour acces rapide lors du calcul des poids de rating.
