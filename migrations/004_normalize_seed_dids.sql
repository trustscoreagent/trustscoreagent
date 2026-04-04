-- Fix: normalize seed data DIDs to match ServiceIdentifier.Normalize() output.
-- The API normalizes "did:web:domain.com" → "domain.com" but the seed data
-- stored the full DID format. This migration fixes the mismatch.

UPDATE services SET did = REPLACE(did, 'did:web:', '') WHERE did LIKE 'did:web:%';
