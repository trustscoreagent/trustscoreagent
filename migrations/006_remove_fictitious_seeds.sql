-- Remove the fictitious *.example.com seed services.
--
-- Migrations 002/005 seeded demo services with invented ratings_count but NO corresponding rows
-- in `ratings` — a reputation registry showing fabricated reputations undermines its own
-- credibility and its audit trail can prove none of them. The registry is now seeded with REAL,
-- auditable measurements by the SeedProber (see src/TrustScore.Api/Jobs/SeedProber.cs).
--
-- Defensive: only delete example.com services that never received a real rating, so no genuine
-- data is ever lost.

DELETE FROM services s
WHERE (s.did LIKE '%.example.com' OR s.did LIKE '%.example.com/%')
  AND NOT EXISTS (SELECT 1 FROM ratings r WHERE r.service_did = s.did);
