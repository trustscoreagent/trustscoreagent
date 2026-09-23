-- Quarantine is decided by how long a target has been failing, not by how many passes failed.
--
-- The pass-count threshold (12) only meant "three days" at one pass every 6 hours. Run the job
-- hourly, as infra/setup-scheduler.sh used to, and the same 12 became twelve hours: short
-- enough for an ordinary outage to be quarantined and hidden, which is the opposite of what the
-- quarantine is for. Recording when the failure streak began makes the rule independent of the
-- schedule.
--
-- Existing streaks are backfilled from their last probe, which can only postpone a quarantine,
-- never bring one forward: when in doubt, keep publishing the measurement.
ALTER TABLE probe_target_health ADD COLUMN IF NOT EXISTS failing_since TIMESTAMPTZ;

UPDATE probe_target_health
SET failing_since = last_probed_at
WHERE consecutive_failures > 0 AND failing_since IS NULL;
