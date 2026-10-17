-- Some x402 endpoints issue a new payee address on every request (one deposit address per
-- payment), and some price each request differently. For them a change is the normal state, not
-- a warning, so the number of valid challenges seen and the run of consecutive observations that
-- each changed are kept: a run of 3 or more marks the payee as rotating (or the price as varying).
ALTER TABLE x402_terms ADD COLUMN IF NOT EXISTS observations  INTEGER NOT NULL DEFAULT 1;
ALTER TABLE x402_terms ADD COLUMN IF NOT EXISTS pay_to_streak INTEGER NOT NULL DEFAULT 0;
ALTER TABLE x402_terms ADD COLUMN IF NOT EXISTS price_streak  INTEGER NOT NULL DEFAULT 0;
