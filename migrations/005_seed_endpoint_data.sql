-- Seed data: add endpoint-level entries for existing providers.
-- Demonstrates that the same provider can have different scores per endpoint.

-- translate.example.com has two endpoints with different quality
INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('translate.example.com/v1/translate', 90.0, 3.0, 92.0, 2.0, 85.0, 5.0, 90.0, 3.0, 180, true, NOW() - INTERVAL '1 hour')
ON CONFLICT (did) DO NOTHING;

INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('translate.example.com/v1/detect-language', 40.0, 15.0, 45.0, 10.0, 30.0, 20.0, 42.0, 12.0, 60, false, NOW() - INTERVAL '6 hours')
ON CONFLICT (did) DO NOTHING;

-- inference.example.com endpoints
INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('inference.example.com/v1/chat', 160.0, 4.0, 158.0, 3.0, 145.0, 8.0, 155.0, 3.0, 450, true, NOW() - INTERVAL '15 minutes')
ON CONFLICT (did) DO NOTHING;

INSERT INTO services (did, alpha, beta, alpha_availability, beta_availability, alpha_latency, beta_latency, alpha_conformity, beta_conformity, ratings_count, supports_receipts, last_rated_at)
VALUES ('inference.example.com/v1/embeddings', 120.0, 10.0, 125.0, 8.0, 100.0, 20.0, 118.0, 12.0, 300, true, NOW() - INTERVAL '45 minutes')
ON CONFLICT (did) DO NOTHING;
