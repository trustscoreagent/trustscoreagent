-- Indexes for queries that currently do sequential scans + sorts.

-- EigenTrust (GetAllRatingsForTrustAsync) filters and the trust/audit jobs order by created_at.
CREATE INDEX IF NOT EXISTS idx_ratings_created_at ON ratings(created_at);

-- Merkle leaf queries (GetAllLeafHashesAsync / GetAnchoredLeafHashesAsync) scan only anchored
-- leaves in deterministic (created_at, id) order. A partial index on that exact shape avoids
-- rebuilding the sort on every /v1/audit/proof request and the hourly anchor.
CREATE INDEX IF NOT EXISTS idx_ratings_merkle_leaves
    ON ratings(created_at, id)
    WHERE merkle_leaf_hash IS NOT NULL;

-- Provider-level score lookups (GetByProviderAsync) do `did LIKE 'provider/%'`. The default PK
-- b-tree can't serve a prefix LIKE; text_pattern_ops can.
CREATE INDEX IF NOT EXISTS idx_services_did_pattern
    ON services(did text_pattern_ops);
