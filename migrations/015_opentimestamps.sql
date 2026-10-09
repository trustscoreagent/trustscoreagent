-- OpenTimestamps proofs of the anchored Merkle roots.
--
-- Each anchor's root is submitted to public OpenTimestamps calendars, which commit it into a
-- Bitcoin transaction within hours, for free. The proof is first "pending" (a calendar's promise)
-- and becomes "bitcoin" once the batch job fetches the completed proof from the calendar. Anyone
-- can then check, with the standard `ots` client and their own Bitcoin node, that the root existed
-- at that block's time, so the log cannot be rewritten afterwards without it showing.
--
-- ots_proof is the serialized timestamp of the root (OpenTimestamps format, without the .ots file
-- header); GET /v1/audit/anchors/{id}/ots wraps it into the file.
ALTER TABLE merkle_anchors ADD COLUMN IF NOT EXISTS ots_proof BYTEA;
ALTER TABLE merkle_anchors ADD COLUMN IF NOT EXISTS ots_status TEXT
    CHECK (ots_status IN ('pending', 'bitcoin'));
ALTER TABLE merkle_anchors ADD COLUMN IF NOT EXISTS ots_bitcoin_height INTEGER;
ALTER TABLE merkle_anchors ADD COLUMN IF NOT EXISTS ots_updated_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS idx_merkle_anchors_ots_pending
    ON merkle_anchors (id) WHERE ots_status IS DISTINCT FROM 'bitcoin';
