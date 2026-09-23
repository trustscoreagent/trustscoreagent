-- Remove agent trust rows that no computation will ever refresh again.
--
-- Until agent signatures existed, every rating was unsigned and EigenTrust stored each agent's
-- trust under its bare DID. Since then, unsigned ratings accrue under 'unsigned:<did>' and the
-- bare DID holds only trust earned by signed ratings. The job upserts and never deletes, so the
-- bare-DID rows written before that change stayed behind, frozen at their last value, and
-- /v1/agent/trust kept serving them as if they were current. The seed probe, for one, showed
-- 0.3776 there while its live score had moved to 0.3376.
--
-- A bare-DID row is only legitimate if the agent has signed at least one rating. Anything else
-- is a leftover. Deleting it makes the lookup fall back to the neutral default for the signed
-- identity, which is the truth, while the agent's real history stays under 'unsigned:<did>'.
-- The next EigenTrust pass recreates any row that should exist.
DELETE FROM agents a
WHERE a.did NOT LIKE 'unsigned:%'
  AND NOT EXISTS (
      SELECT 1 FROM ratings r
      WHERE r.agent_did = a.did AND r.signature_verified
  );
