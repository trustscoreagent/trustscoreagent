-- The x402 endpoints the batch job probes.
--
-- Picked once a day from the public x402 Bazaar catalog (Coinbase CDP): endpoints that real
-- payers used in the last 30 days, a capped number per host. Each pass sends one request without
-- payment and records what the 402 challenge says, so the registry has measurements on the
-- endpoints agents actually pay, not only on free public APIs.
--
-- Rows are never deleted: an endpoint that drops out of the selection is marked inactive and
-- stops being probed, and comes back if it is selected again. The ratings it produced stay in the
-- audit log like any other.
CREATE TABLE IF NOT EXISTS x402_probe_targets (
    service_did    TEXT PRIMARY KEY,              -- ServiceIdentifier.Normalize(url)
    url            TEXT        NOT NULL,          -- the resource exactly as the catalog lists it
    method         TEXT        NOT NULL,          -- GET, POST or HEAD, as the catalog declares
    unique_payers  INTEGER     NOT NULL,          -- distinct payers over 30 days, at selection
    active         BOOLEAN     NOT NULL DEFAULT TRUE,
    selected_at    TIMESTAMPTZ NOT NULL,          -- when it was last part of a selection
    refreshed_at   TIMESTAMPTZ NOT NULL           -- when the selection that wrote this row ran
);

CREATE INDEX IF NOT EXISTS idx_x402_probe_targets_active
    ON x402_probe_targets (service_did)
    WHERE active;
