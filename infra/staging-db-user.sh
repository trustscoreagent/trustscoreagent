#!/usr/bin/env bash
# Run once per project, by an operator: it changes database roles and a secret.
# Give staging its own database user, owning only trustscore_staging, and stop PUBLIC from
# connecting to either database. Secrets are never printed.
#
# Needs: the Cloud SQL proxy listening on 127.0.0.1:5439 (for example:
#   ./cloud-sql-proxy.exe --port 5439 --gcloud-auth trustscoreagent:europe-west1:trustscoreagent-db)
# and psql (set PSQL to its path if it is not on PATH).
set -euo pipefail
cd "$(dirname "$0")/sql"
PSQL="${PSQL:-psql}"
P=trustscoreagent

field() { tr ';' '\n' | grep -i "^$1=" | head -1 | cut -d= -f2-; }

PROD_CS=$(gcloud secrets versions access latest --secret db-connection-string --project $P)
export PGHOST=127.0.0.1 PGPORT=5439 PGSSLMODE=disable
export PGUSER=$(echo "$PROD_CS" | field Username)
export PGPASSWORD=$(echo "$PROD_CS" | field Password)
NEWPW=$(python -c "import secrets;print(secrets.token_hex(24))")

echo "[1/4] Creating trustscore_staging and handing it the staging database"
"$PSQL" -d trustscore_staging -At -v pw="$NEWPW" -f staging-db-user.sql

echo "[2/4] Writing the new staging connection string (new secret version)"
echo "$PROD_CS" | NEWPW="$NEWPW" python -c "
import os, sys
out = []
for part in [p for p in sys.stdin.read().strip().split(';') if p.strip()]:
    k, v = part.split('=', 1)
    key = k.strip().lower()
    if key == 'database': v = 'trustscore_staging'
    elif key in ('username', 'user id', 'user'): v = 'trustscore_staging'
    elif key == 'password': v = os.environ['NEWPW']
    out.append(k + '=' + v)
sys.stdout.write(';'.join(out))" \
  | gcloud secrets versions add db-connection-string-staging --project $P --data-file=-

echo "[3/4] Checking what the new user can reach"
export PGUSER=trustscore_staging PGPASSWORD="$NEWPW"
"$PSQL" -d trustscore_staging -At -c "select 'staging ok: ' || count(*) || ' ratings' from ratings;"
if "$PSQL" -d trustscore -At -c "select 1" >/dev/null 2>&1; then
  echo "ERROR: trustscore_staging can still open the production database" >&2; exit 1
else
  echo "prod refused for trustscore_staging: ok"
fi

echo "[4/4] Checking production still works with its own user"
export PGUSER=$(echo "$PROD_CS" | field Username) PGPASSWORD=$(echo "$PROD_CS" | field Password)
"$PSQL" -d trustscore -At -c "select 'prod ok: ' || count(*) || ' ratings' from ratings;"
unset PGPASSWORD NEWPW PROD_CS
echo "Done. Staging picks the new credentials on its next revision (next staging deploy)."
