-- Track the health of each seed-probe target so a rotted probe URL stops being published as a
-- service outage.
--
-- A probe URL that has moved or been deprecated is indistinguishable, from the response alone,
-- from a service that is genuinely down: both just fail. That already happened at 21 targets
-- (frankfurter moved domains, restcountries was deprecated) and was only caught by hand. At a
-- few dozen targets nobody notices, and the registry quietly publishes that a healthy third
-- party is unreliable. For a project whose whole claim is genuine, auditable measurement, a
-- false accusation against a real company is the worst possible failure.
--
-- The one signal that separates the two is duration. A real outage lasts hours; a dead URL
-- fails forever. So a target that has failed every probe for days is treated as our
-- configuration being wrong rather than as evidence about the service, and stops producing
-- ratings until it answers again.
--
-- Ratings already written are left alone: the audit log records what was measured at the time,
-- and rewriting it would be worse than the imprecision. We simply stop adding more once we no
-- longer trust the probe.
CREATE TABLE IF NOT EXISTS probe_target_health (
    service_did          TEXT PRIMARY KEY,
    consecutive_failures INTEGER     NOT NULL DEFAULT 0,
    -- Non-null means the target is quarantined: still probed every run, so it can recover on
    -- its own, but not recorded as a rating.
    quarantined_at       TIMESTAMPTZ,
    last_status_code     INTEGER,
    last_probed_at       TIMESTAMPTZ NOT NULL
);

-- Supports "what is currently quarantined", which is the question asked when the digest looks
-- wrong. Partial, because quarantine is meant to be the rare case.
CREATE INDEX IF NOT EXISTS idx_probe_target_health_quarantined
    ON probe_target_health (quarantined_at)
    WHERE quarantined_at IS NOT NULL;
