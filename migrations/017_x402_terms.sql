-- The payment terms each probed x402 endpoint asks for, and how stable they are.
--
-- Every probe pass that gets a valid 402 challenge compares its ways to pay with the ones seen
-- before. A new payee address (payTo) or a new price is the signal a payer most needs before
-- sending money to an endpoint it used yesterday, so the date of the last change and the number
-- of changes are kept per endpoint, and each change is kept with what it replaced.
--
-- accepts holds [{scheme, network, asset, amount, pay_to}], deduplicated and sorted.
CREATE TABLE IF NOT EXISTS x402_terms (
    service_did        TEXT PRIMARY KEY,           -- ServiceIdentifier.Normalize(url)
    x402_version       INTEGER     NOT NULL,       -- 1 or 2, as the last challenge declared
    accepts            JSONB       NOT NULL,       -- the ways to pay last seen
    first_seen_at      TIMESTAMPTZ NOT NULL,       -- first valid challenge
    last_seen_at       TIMESTAMPTZ NOT NULL,       -- latest valid challenge
    pay_to_changed_at  TIMESTAMPTZ,                -- null: never seen changing
    pay_to_changes     INTEGER     NOT NULL DEFAULT 0,
    price_changed_at   TIMESTAMPTZ,                -- null: never seen changing
    price_changes      INTEGER     NOT NULL DEFAULT 0
);

-- Append-only: one row per observed change.
CREATE TABLE IF NOT EXISTS x402_terms_changes (
    id                 BIGSERIAL PRIMARY KEY,
    service_did        TEXT        NOT NULL,
    observed_at        TIMESTAMPTZ NOT NULL,
    pay_to_changed     BOOLEAN     NOT NULL,
    price_changed      BOOLEAN     NOT NULL,
    previous_accepts   JSONB       NOT NULL,
    accepts            JSONB       NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_x402_terms_changes_service
    ON x402_terms_changes (service_did, observed_at);
