-- Normalize service DIDs to match ServiceIdentifier.Normalize() output:
-- "did:web:domain.com" → "domain.com".
--
-- Written defensively. A bare `UPDATE services SET did = ...` fails in two ways on a populated
-- database (and DbUp runs migrations at startup, so a failure blocks every deploy):
--   1. ratings.service_did has a FOREIGN KEY to services(did) without ON UPDATE CASCADE, so
--      renaming the primary key violates the constraint.
--   2. The normalized DID may already exist (e.g. created by the API), causing a PK collision.
-- The steps below keep the foreign key satisfied at every point and merge any duplicates.

BEGIN;

-- 1. Where a "did:web:" service and its normalized twin both exist: repoint ratings to the twin,
--    then delete the now-orphaned prefixed duplicate.
UPDATE ratings r
SET service_did = regexp_replace(r.service_did, '^did:web:', '')
WHERE r.service_did LIKE 'did:web:%'
  AND EXISTS (SELECT 1 FROM services s WHERE s.did = regexp_replace(r.service_did, '^did:web:', ''));

DELETE FROM services s
WHERE s.did LIKE 'did:web:%'
  AND EXISTS (SELECT 1 FROM services t WHERE t.did = regexp_replace(s.did, '^did:web:', ''));

-- 2. For the remaining prefixed services (no normalized twin yet): create the normalized row
--    first, repoint ratings to it, then drop the old prefixed row. Insert-before-repoint keeps
--    the ratings.service_did foreign key valid throughout.
INSERT INTO services (did, alpha, beta,
    alpha_availability, beta_availability,
    alpha_latency, beta_latency,
    alpha_conformity, beta_conformity,
    ratings_count, supports_receipts, last_rated_at, created_at, updated_at)
SELECT regexp_replace(did, '^did:web:', ''), alpha, beta,
    alpha_availability, beta_availability,
    alpha_latency, beta_latency,
    alpha_conformity, beta_conformity,
    ratings_count, supports_receipts, last_rated_at, created_at, updated_at
FROM services
WHERE did LIKE 'did:web:%'
ON CONFLICT (did) DO NOTHING;

UPDATE ratings
SET service_did = regexp_replace(service_did, '^did:web:', '')
WHERE service_did LIKE 'did:web:%';

DELETE FROM services WHERE did LIKE 'did:web:%';

COMMIT;
