\set ON_ERROR_STOP on
BEGIN;
CREATE ROLE trustscore_staging LOGIN PASSWORD :'pw';
-- Lets the current (prod) user hand the objects over; it could already reach this database.
GRANT trustscore_staging TO trustscore;
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND pg_get_userbyid(c.relowner) = 'trustscore'
             AND c.relkind IN ('r','p','v','m','f')
  LOOP
    EXECUTE format('ALTER TABLE public.%I OWNER TO trustscore_staging', r.relname);
  END LOOP;
  FOR r IN SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND pg_get_userbyid(c.relowner) = 'trustscore' AND c.relkind = 'S'
  LOOP
    EXECUTE format('ALTER SEQUENCE public.%I OWNER TO trustscore_staging', r.relname);
  END LOOP;
END $$;
ALTER DATABASE trustscore_staging OWNER TO trustscore_staging;
-- By default every role may connect to every database. Staging's user must not reach prod.
-- trustscore and postgres keep access to prod through cloudsqlsuperuser, which owns it.
REVOKE CONNECT, TEMPORARY ON DATABASE trustscore_staging FROM PUBLIC;
REVOKE CONNECT, TEMPORARY ON DATABASE trustscore FROM PUBLIC;
COMMIT;
SELECT 'still owned by trustscore in staging: ' || count(*) FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
 WHERE n.nspname = 'public' AND pg_get_userbyid(c.relowner) = 'trustscore';
