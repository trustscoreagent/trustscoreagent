# EigenTrust : confiance des agents evaluateurs

> Implemente dans `src/TrustScore.Api/Scoring/EigenTrustEngine.cs`, execute par le job batch
> toutes les 6 heures. Ce document garde les decisions de conception ; la version publique, en
> anglais, est [trust.md](./trust.md).

## Principe

Chaque agent evaluateur a une confiance (0.1 a 1.0) qui multiplie le poids de ses notes. Elle est
ancree sur des **pairs de confiance** fixes (l'approche d'EigenTrust, Kamvar et al. 2003) et se
propage aux agents dont les observations concordent avec celles des agents de confiance.

## Algorithme

```
1. Un vote par (agent, service, nature d'observation) : succes / sous le seuil de latence
   (2000 ms, celui du score) / schema valide, a la majorite des notes de l'agent.
   Nature : 402 = challenge de paiement non paye ; autre statut = resultat d'un appel.
2. Confiance initiale : pairs de confiance 1.0, tous les autres 0.25.
3. Repeter jusqu'a convergence (20 tours max, ecart < 1e-4) :
   a. Consensus par (service, nature) : si un pair de confiance l'a observe, c'est son
      observation ; sinon vote des autres agents pondere par max(0, confiance - 0.25).
   b. Chaque agent (hors pairs de confiance) est compare au consensus SANS son propre vote ;
      un sujet ou plus personne d'autre ne pese ne compte pas.
   c. confiance = 0.25 + (cible - 0.25) * min(1, compares / 10),
      cible = 0.1 + 0.8 * taux d'accord.
```

## Decisions

### Pair de confiance : la sonde du registre (decision du 2026-10-26)
`unsigned:did:web:trustscoreagent.com:probe` (la sonde note sans signature), confiance fixe 1.0.
Elle ne mesure que des faits objectifs et son code et ses notes sont publics. Contrepartie
assumee et documentee : l'operateur du registre pese plus que les autres evaluateurs. Les DID
`did:web:` du domaine `trustscoreagent.com` sont refuses en entree de `POST /v1/rate`
(`reserved_agent_did`), sinon n'importe qui pourrait noter sous l'identite de la sonde.

Avant : les seeds etaient les 10 agents ayant le plus de recus verifies. Un recu est signe par le
service note lui-meme, et la prod n'en avait aucun : un seul recu auto-emis faisait de son auteur
l'unique seed (audit 2026-10, C1). Les recus ne jouent plus aucun role dans la confiance.
A terme (quand des services emettront de vrais recus), des agents ayant des recus de plusieurs
services independants pourront devenir pairs de confiance a leur tour.

### Deux natures d'observation
La sonde voit le challenge 402 d'un endpoint x402 sans payer. Un agent qui a paye et recu une 500
rapporte ce que la sonde ne peut pas voir : comparer les deux le punirait pour le signal que le
registre doit justement proteger. Challenge et resultat d'appel ne sont donc jamais compares.

### Latence jugee au seuil du score
Concordance = meme cote du seuil de 2000 ms, et non "dans un facteur 2 de la mediane" : un agent
loin du service (150 ms contre 40 ms pour la sonde) n'est pas incoherent pour autant.

### Un vote par agent, rien pour les nouveaux
Le consensus ne compte que la confiance gagnee au-dessus du defaut : une foule d'identites neuves
ne vote pas, une clique isolee ne peut pas se certifier elle-meme (aucun de ses sujets n'a de
consensus), et noter cent fois la meme chose reste un seul vote.

### Confiance par defaut : 0.25
Sous le milieu 0.5 parce qu'une cle est gratuite. Un vrai nouvel agent n'est pas bloque : il
remonte au batch suivant (6 h) s'il note des services deja observes, jusqu'a 0.9 apres 10.

### Poids d'une note
```
poids = base x identite x confiance / n
base = 1.0 si recu verifie couvrant l'appel ET agent ayant gagne de la confiance, sinon 0.3
identite = 1 si signe, 0.5 sinon
n = rang de la note du meme domaine par la meme source (adresse, et cle si signe) en 24 h
```
Plus l'allocation des nouveaux venus : par domaine et par 24 h, seules les 5 premieres notes
d'agents sans confiance gagnee comptent (les suivantes sont enregistrees a poids 0). Detail et
chiffres de simulation : [trust.md](./trust.md).

### Plafond
5000 agents (les plus actifs, pairs de confiance toujours inclus) ; le reste a la confiance par
defaut. 100 000 notes les plus recentes sur 90 jours.

### Visibilite
`GET /v1/agent/trust?did={did}` repond pour n'importe quel DID : `trust_score` (identite signee)
et `unsigned_trust_score` (notes non signees sous ce DID).

### Stockage
Table `agents`, champ `trust_score`, lu a travers un cache Redis (TTL 1 h).
