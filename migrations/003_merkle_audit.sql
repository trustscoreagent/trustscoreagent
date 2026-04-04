-- Merkle tree audit log
-- Stores the root hash anchored periodically (hourly)

CREATE TABLE IF NOT EXISTS merkle_anchors (
    id SERIAL PRIMARY KEY,
    merkle_root TEXT NOT NULL,
    leaf_count INTEGER NOT NULL,
    first_rating_id UUID,
    last_rating_id UUID,
    anchored_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    -- Blockchain anchoring (null until anchored on-chain)
    blockchain TEXT,
    contract_address TEXT,
    transaction_hash TEXT,
    block_number BIGINT
);

CREATE INDEX IF NOT EXISTS idx_merkle_anchors_time ON merkle_anchors(anchored_at DESC);

-- Add merkle_leaf_hash to ratings table (computed at insertion time)
ALTER TABLE ratings ADD COLUMN IF NOT EXISTS merkle_leaf_hash TEXT;
