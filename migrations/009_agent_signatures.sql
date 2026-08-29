-- Record whether a rating carried a verified agent signature (X-Agent-Signature).
--
-- Until now the submitting agent was identified by a bare X-Agent-DID header, which is
-- self-asserted: anyone could rate under another agent's identity, or mint unlimited identities to
-- outvote honest ones. Ratings can now be signed with the Ed25519 key behind that did:key, proving
-- the sender holds it.
--
-- Unsigned ratings stay valid (existing clients keep working) but count for less, so this column is
-- what tells the two apart after the fact: it is the difference between "an agent claiming to be X
-- said this" and "X said this". Defaults to false, which is the correct reading of every row
-- written before this migration.
ALTER TABLE ratings ADD COLUMN IF NOT EXISTS signature_verified BOOLEAN NOT NULL DEFAULT FALSE;

-- Supports "how much of our data is cryptographically attributable" without scanning the table.
CREATE INDEX IF NOT EXISTS idx_ratings_signature_verified
    ON ratings (signature_verified)
    WHERE signature_verified = TRUE;
