-- Take services on non-public hosts out of the public list.
--
-- On 2026-10-03 and 2026-10-04 a client sent six ratings (User-Agent "node", each under a new
-- signed did:key, the profile of an MCP server run in a throwaway sandbox) on services at
-- 127.0.0.1: the rater's own machine, which nobody else can reach. They are kept for the same
-- reason as in migration 014: their ratings are leaves of the anchored Merkle log, and deleting
-- them would break every later consistency proof. They still answer GET /v1/score but are no
-- longer listed by GET /v1/services. Since this release POST /v1/rate refuses such hosts
-- (PublicHost.IsPublic), so no new ones can appear.
UPDATE services SET listed = FALSE
WHERE did IN (
    '127.0.0.1/control',
    '127.0.0.1/t/f007112/1/control',
    '127.0.0.1/t/f007112/2/control',
    '127.0.0.1/t/f007112/3/control'
);
