# Merkle Tree & Blockchain Anchoring — Specification

> Audit log transparent et immuable. Prouve que personne (meme nous)
> ne peut supprimer ou modifier un rating apres coup.

## Principe

Chaque rating accepte est hashe et ajoute a un arbre de Merkle append-only.
La racine de l'arbre est publiee toutes les heures sur Base L2 (blockchain).
N'importe qui peut verifier qu'un rating specifique est dans l'arbre (inclusion proof).

## Decisions

### Contenu des feuilles

Pas d'agent_did dans le hash (RGPD-safe). Le rating_id permet de retrouver
le detail dans la DB si necessaire.

```
leaf = SHA256(rating_id + service_did + timestamp)
```

Si l'agent exerce son droit a l'effacement, on supprime ses donnees dans PostgreSQL.
Le hash reste dans le Merkle tree mais ne peut plus etre relie a une personne.

### Frequence d'ancrage

**Toutes les heures** via Cloud Run Job (meme job que EigenTrust, ou job separe).
Le cout est fixe et independant du volume :

| Volume de ratings | Ancrages/mois | Cout Base L2/mois |
|---|---|---|
| 100 | 720 | ~0.70$ |
| 100 000 000 | 720 | ~0.70$ |

### Blockchain : Base L2

- Frais les plus bas des L2 Ethereum
- Ecosysteme agents actif (Coinbase AgentKit)
- Transaction : publication d'un hash de 32 bytes

### Smart contract

Minimal, ~10 lignes de Solidity :

```solidity
// SPDX-License-Identifier: Apache-2.0
pragma solidity ^0.8.20;

contract TrustScoreAnchor {
    event RootAnchored(
        bytes32 indexed root,
        uint256 indexed blockNumber,
        uint256 ratingCount,
        uint256 timestamp
    );

    address public immutable operator;
    bytes32[] public roots;

    constructor() {
        operator = msg.sender;
    }

    function anchor(bytes32 root, uint256 ratingCount) external {
        require(msg.sender == operator, "unauthorized");
        roots.push(root);
        emit RootAnchored(root, block.number, ratingCount, block.timestamp);
    }

    function getRootsCount() external view returns (uint256) {
        return roots.length;
    }

    function getRoot(uint256 index) external view returns (bytes32) {
        return roots[index];
    }
}
```

### Wallet

- Cle privee Ethereum generee une fois, stockee dans GCP Secret Manager
- Adresse publique alimentee en ~5$ d'ETH sur Base (dure des annees)
- Le Cloud Run Job signe et publie la transaction toutes les heures

### APIs de verification

**MVP** : un seul endpoint

```
GET /v1/audit/root

Response:
{
  "merkle_root": "0xabc123...",
  "rating_count": 15432,
  "anchored_at": "2026-04-03T14:00:00Z",
  "blockchain": "base",
  "contract_address": "0xdef456...",
  "transaction_hash": "0x789abc...",
  "block_number": 12345678
}
```

**Phase 2** : endpoints supplementaires

```
GET /v1/audit/proof/{rating_id}
→ Inclusion proof : liste de hashes pour verifier qu'un rating est dans l'arbre

GET /v1/audit/consistency?from={block}&to={block}
→ Consistency proof : prouver que l'arbre n'a pas ete altere entre deux ancrages
```

### RGPD

- Les feuilles contiennent `SHA256(rating_id + service_did + timestamp)` — pas de donnee personnelle
- L'agent_did n'est PAS dans le hash
- Les donnees personnelles (agent_did) sont dans PostgreSQL et supprimables
- Le hash orphelin dans le Merkle tree ne peut pas etre relie a une personne
- Pas de conflit RGPD

### Implementation

**Merkle tree en C#** : implementation custom avec `System.Security.Cryptography.SHA256`.
Stockage de l'arbre dans PostgreSQL (table `merkle_nodes` ou en memoire au moment du calcul).

**Ancrage blockchain** : library `Nethereum` (C#, NuGet) pour signer et envoyer
la transaction sur Base L2.

**Job** : Cloud Run Job declenche par Cloud Scheduler toutes les heures.
1. Charger tous les ratings depuis le dernier ancrage
2. Construire/mettre a jour le Merkle tree
3. Signer et publier la racine sur Base L2
4. Stocker la reference (tx hash, block number) dans PostgreSQL

### Setup initial (une seule fois)

1. Deployer le smart contract sur Base L2
2. Generer un wallet Ethereum (cle privee)
3. Alimenter le wallet en ETH sur Base (~5$)
4. Stocker la cle privee dans GCP Secret Manager
5. Stocker l'adresse du contrat dans la config
