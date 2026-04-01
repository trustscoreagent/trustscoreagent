CREATE TABLE IF NOT EXISTS services (
    did TEXT PRIMARY KEY,
    alpha DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    beta DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    alpha_availability DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    beta_availability DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    alpha_latency DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    beta_latency DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    alpha_conformity DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    beta_conformity DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    ratings_count INTEGER NOT NULL DEFAULT 0,
    supports_receipts BOOLEAN NOT NULL DEFAULT false,
    last_rated_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS ratings (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    service_did TEXT NOT NULL REFERENCES services(did),
    agent_did TEXT NOT NULL,
    status_code INTEGER NOT NULL,
    latency_ms INTEGER NOT NULL,
    response_size_bytes INTEGER,
    schema_valid BOOLEAN,
    quality_score SMALLINT CHECK (quality_score BETWEEN 1 AND 5),
    comment TEXT,
    has_receipt BOOLEAN NOT NULL DEFAULT false,
    receipt_verified BOOLEAN NOT NULL DEFAULT false,
    weight DOUBLE PRECISION NOT NULL DEFAULT 0.3,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_ratings_service ON ratings(service_did, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_ratings_agent ON ratings(agent_did, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_ratings_ratelimit ON ratings(agent_did, service_did, created_at DESC);

CREATE TABLE IF NOT EXISTS agents (
    did TEXT PRIMARY KEY,
    ratings_count INTEGER NOT NULL DEFAULT 0,
    trust_score DOUBLE PRECISION NOT NULL DEFAULT 0.5,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
