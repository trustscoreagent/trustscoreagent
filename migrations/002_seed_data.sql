-- Seed data: example services with realistic reputation scores
-- This gives visitors something to see when they first explore the API.

-- A well-rated translation service
INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('did:web:translate.example.com', 85.0, 4.0, 90.0, 3.0, 78.0, 8.0, 88.0, 5.0, 200, true, NOW() - INTERVAL '2 hours')
ON CONFLICT (did) DO NOTHING;

-- A decent scraping service
INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('did:web:scraper.example.com', 45.0, 12.0, 50.0, 8.0, 35.0, 18.0, 48.0, 10.0, 120, false, NOW() - INTERVAL '5 hours')
ON CONFLICT (did) DO NOTHING;

-- An excellent AI inference API
INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('did:web:inference.example.com', 150.0, 5.0, 155.0, 3.0, 140.0, 10.0, 148.0, 4.0, 500, true, NOW() - INTERVAL '30 minutes')
ON CONFLICT (did) DO NOTHING;

-- A mediocre data enrichment service
INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('did:web:enrich.example.com', 20.0, 15.0, 22.0, 12.0, 18.0, 20.0, 25.0, 10.0, 80, false, NOW() - INTERVAL '1 day')
ON CONFLICT (did) DO NOTHING;

-- A new service with very few ratings
INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('did:web:newapi.example.com', 3.0, 1.0, 3.0, 1.0, 3.0, 1.0, 3.0, 1.0, 5, false, NOW() - INTERVAL '3 days')
ON CONFLICT (did) DO NOTHING;

-- A problematic service with poor reliability
INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('did:web:unreliable.example.com', 10.0, 30.0, 8.0, 35.0, 12.0, 25.0, 10.0, 28.0, 90, false, NOW() - INTERVAL '12 hours')
ON CONFLICT (did) DO NOTHING;
