-- Merkle v2: record which leaf format each rating was committed with, and which tree algorithm
-- each anchor was built with.
--
-- A v1 leaf commits to (id, service_did, created_at) only; a v2 leaf commits to every field that
-- feeds the score (see RatingLeaf). A v1 tree duplicates an odd node and does not separate leaves
-- from interior nodes; a v2 tree uses RFC 6962-style hashing.
--
-- Both default to 1 because that is exactly what every existing row is, and what the previous
-- application version keeps writing until it is replaced: during a rolling deploy an old instance
-- inserts a v1 leaf with leaf_version 1, which is correct. Existing ratings are never re-hashed as
-- v2; their content was not committed when they were written.
ALTER TABLE ratings ADD COLUMN IF NOT EXISTS leaf_version SMALLINT NOT NULL DEFAULT 1;
ALTER TABLE merkle_anchors ADD COLUMN IF NOT EXISTS tree_version SMALLINT NOT NULL DEFAULT 1;
