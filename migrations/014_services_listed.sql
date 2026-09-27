-- Let a service stay scored but out of the public list.
--
-- A few services in production were created by tests run against the live API (a Redis upgrade
-- check on 2026-08-03, a failure-handling check on 2026-08-15). They cannot be deleted: their
-- ratings are leaves of the anchored Merkle log, and removing them would make every later
-- consistency proof fail, which is exactly the tampering signal the log exists to raise. So they
-- are kept, still answer GET /v1/score, and are simply no longer listed by GET /v1/services.
--
-- New services are listed by default. To unlist one later, set listed = FALSE with a migration
-- that says why.
ALTER TABLE services ADD COLUMN IF NOT EXISTS listed BOOLEAN NOT NULL DEFAULT TRUE;

UPDATE services SET listed = FALSE
WHERE did IN (
    'redis31-prodtest.example.org',
    'redis31-test.example.org',
    'api.slow-service.net',
    'api.timeout-edge.com',
    'api.failing-service.com'
);
