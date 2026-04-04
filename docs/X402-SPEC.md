# x402 Micropayments — Specification (Phase 2)

> A implementer quand on a du trafic et que la monetisation devient necessaire.
> Les endpoints premium sont deja codes et fonctionnels — il reste a ajouter le paywall.

## Endpoints concernes

| Endpoint | Prix | Description |
|----------|------|-------------|
| `GET /v1/score/history` | 0.001 USDC | Historique de scores agrege par jour |
| `GET /v1/score/detailed` | 0.001 USDC | Percentiles latence, distribution qualite, stats receipts |
| `POST /v1/scores/bulk` | 0.05 USDC | Jusqu'a 100 scores en un appel |
| `POST /v1/alerts` | 0.01 USDC | Setup webhook d'alerte (pas encore implemente) |

Les endpoints de base (score, rate, services, audit) restent **gratuits a vie**.

## Protocole x402

Flux standard HTTP 402 :

```
Agent → GET /v1/score/history?did=...
     ← 402 Payment Required
       X-Payment-Amount: 1000          (0.001 USDC, 6 decimales)
       X-Payment-Currency: USDC
       X-Payment-Network: base
       X-Payment-Address: 0xabc...     (notre wallet de reception)
       X-Payment-Expiry: 300           (secondes avant expiration)

Agent → transfert 0.001 USDC on-chain vers 0xabc...

Agent → GET /v1/score/history?did=...
       X-Payment-Proof: 0xtxhash...   (hash de la transaction)
     ← 200 OK + donnees
```

## Implementation necessaire

### 1. Middleware x402 (C#)

Intercepte les requetes sur les endpoints premium :
- Si pas de header `X-Payment-Proof` → retourne 402 avec les headers de paiement
- Si header present → verifie la transaction on-chain → si OK, laisse passer

```csharp
// Pseudo-code du middleware
public class X402Middleware
{
    public async Task InvokeAsync(HttpContext context)
    {
        var proof = context.Request.Headers["X-Payment-Proof"].FirstOrDefault();
        if (proof is null)
        {
            context.Response.StatusCode = 402;
            context.Response.Headers["X-Payment-Amount"] = price.ToString();
            context.Response.Headers["X-Payment-Currency"] = "USDC";
            context.Response.Headers["X-Payment-Network"] = "base";
            context.Response.Headers["X-Payment-Address"] = walletAddress;
            return;
        }

        var isValid = await VerifyPaymentOnChain(proof, expectedAmount);
        if (!isValid)
        {
            context.Response.StatusCode = 402;
            return;
        }

        await _next(context);
    }
}
```

### 2. Verification on-chain

Verifier qu'une transaction USDC existe sur Base L2 :
- Appel RPC via **Nethereum** (NuGet, C# Ethereum lib)
- Endpoint RPC : Alchemy Free (300M compute units/mois) ou RPC public Base
- Verifier : destinataire = notre adresse, montant >= prix, token = USDC, confirmee

### 3. Wallet de reception

- Adresse Ethereum dediee pour les revenus (separee du wallet d'anchoring Merkle)
- Cle privee dans GCP Secret Manager
- Pas besoin d'ETH dessus (on recoit, on ne depense pas)

### 4. Tracking des paiements

Nouvelle table PostgreSQL :

```sql
CREATE TABLE payments (
    id SERIAL PRIMARY KEY,
    transaction_hash TEXT UNIQUE NOT NULL,
    amount_usdc DECIMAL(18,6) NOT NULL,
    payer_address TEXT NOT NULL,
    endpoint TEXT NOT NULL,
    verified_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

Evite le double-spend : si un tx_hash a deja ete utilise, rejeter.

### 5. Cout du gas pour l'agent

Un transfert USDC sur Base L2 coute ~0.0001$ de gas.
Pour un paiement de 0.001$, le gas represente ~10%.
Plancher recommande : 0.001$ minimum par appel.

## Prerequisites

- [ ] Wallet Ethereum pour les revenus USDC (cle dans Secret Manager)
- [ ] Compte Alchemy (free tier) pour les appels RPC Base L2
- [ ] Package NuGet Nethereum
- [ ] Contrat USDC sur Base : 0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913
- [ ] Middleware x402 dans l'API
- [ ] Table payments dans PostgreSQL
- [ ] Tests avec des transactions reelles sur Base testnet d'abord
