#!/bin/bash
# ============================================================================
# TrustScoreAgent — GCP Infrastructure Setup
# ============================================================================
# Run this script ONCE to create all GCP resources.
# Prerequisites: gcloud CLI installed and authenticated.
#
# Usage:
#   chmod +x infra/setup-gcp.sh
#   ./infra/setup-gcp.sh
# ============================================================================

set -euo pipefail

PROJECT_ID="trustscoreagent"
REGION="europe-west1"
DB_INSTANCE="trustscoreagent-db"
DB_NAME="trustscore"
DB_USER="trustscore"
DB_PASSWORD=$(openssl rand -base64 24)
REDIS_INSTANCE="trustscoreagent-cache"
ARTIFACT_REPO="trustscoreagent"
SA_NAME="github-actions"
SA_EMAIL="${SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

echo "=== TrustScoreAgent GCP Setup ==="
echo "Project: $PROJECT_ID"
echo "Region: $REGION"
echo ""

# -------------------------------------------------------
# 1. Set project
# -------------------------------------------------------
echo "[1/9] Setting project..."
gcloud config set project "$PROJECT_ID"

# -------------------------------------------------------
# 2. Enable APIs
# -------------------------------------------------------
echo "[2/9] Enabling APIs..."
gcloud services enable \
  run.googleapis.com \
  sqladmin.googleapis.com \
  secretmanager.googleapis.com \
  artifactregistry.googleapis.com \
  vpcaccess.googleapis.com \
  redis.googleapis.com \
  iam.googleapis.com \
  iamcredentials.googleapis.com \
  --quiet

# -------------------------------------------------------
# 3. Create Artifact Registry repository (Docker images)
# -------------------------------------------------------
echo "[3/9] Creating Artifact Registry..."
gcloud artifacts repositories create "$ARTIFACT_REPO" \
  --repository-format=docker \
  --location="$REGION" \
  --description="TrustScoreAgent Docker images" \
  --quiet 2>/dev/null || echo "  (already exists)"

# -------------------------------------------------------
# 4. Create Cloud SQL PostgreSQL instance
# -------------------------------------------------------
echo "[4/9] Creating Cloud SQL instance (this takes ~5 minutes)..."
gcloud sql instances create "$DB_INSTANCE" \
  --database-version=POSTGRES_16 \
  --tier=db-f1-micro \
  --region="$REGION" \
  --storage-size=10GB \
  --storage-auto-increase \
  --backup-start-time=03:00 \
  --enable-point-in-time-recovery \
  --no-assign-ip \
  --quiet 2>/dev/null || echo "  (already exists)"

echo "  Creating database..."
gcloud sql databases create "$DB_NAME" \
  --instance="$DB_INSTANCE" \
  --quiet 2>/dev/null || echo "  (already exists)"

# Re-running this script must not rotate the database password: every other secret derived from
# it (db-connection-string-staging is a copy with another database) would silently go stale and
# break staging. So the password is only set when the connection-string secret does not exist.
DB_CONNECTION_NAME=$(gcloud sql instances describe "$DB_INSTANCE" --format='value(connectionName)')
if gcloud secrets describe db-connection-string >/dev/null 2>&1; then
  echo "  db-connection-string already exists: leaving the database password unchanged"
  DB_CONNECTION_STRING=""
else
  echo "  Creating the database user..."
  gcloud sql users create "$DB_USER" \
    --instance="$DB_INSTANCE" \
    --password="$DB_PASSWORD" \
    --quiet 2>/dev/null || \
  gcloud sql users set-password "$DB_USER" \
    --instance="$DB_INSTANCE" \
    --password="$DB_PASSWORD" \
    --quiet
  DB_CONNECTION_STRING="Host=/cloudsql/${DB_CONNECTION_NAME};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD}"
fi

# -------------------------------------------------------
# 5. Redis — external serverless (Upstash), not Memorystore
# -------------------------------------------------------
# Memorystore has a ~1GB minimum (Basic tier) that costs ~€35/mo even when idle.
# Redis is now provided by Upstash (serverless, pay-per-request, ~€0 at low
# volume). Create a Regional Redis DB at https://upstash.com (region close to
# $REGION, e.g. eu-central-1; TLS enabled) and export its StackExchange.Redis
# connection string before running this script:
#   export REDIS_CONNECTION_STRING="HOST:6379,password=...,ssl=True,abortConnect=False"
echo "[5/9] Redis: using external Upstash endpoint from \$REDIS_CONNECTION_STRING."
: "${REDIS_CONNECTION_STRING:?Set REDIS_CONNECTION_STRING to your Upstash connection string (HOST:6379,password=...,ssl=True,abortConnect=False)}"

# -------------------------------------------------------
# 6. No VPC connector needed
# -------------------------------------------------------
# Cloud Run reaches Cloud SQL via the built-in socket connector and Upstash over
# public egress, so no Serverless VPC Access connector is required (it also cost
# ~€8-10/mo for its minimum instances).
echo "[6/9] No VPC connector needed (Cloud SQL via socket, Redis via public egress)."

# -------------------------------------------------------
# 7. Store secrets in Secret Manager
# -------------------------------------------------------
echo "[7/9] Storing secrets..."
if [ -n "$DB_CONNECTION_STRING" ]; then
  echo -n "$DB_CONNECTION_STRING" | gcloud secrets create db-connection-string \
    --data-file=- \
    --replication-policy=automatic \
    --quiet
fi

echo -n "$REDIS_CONNECTION_STRING" | gcloud secrets create redis-connection-string \
  --data-file=- \
  --replication-policy=automatic \
  --quiet 2>/dev/null || \
echo -n "$REDIS_CONNECTION_STRING" | gcloud secrets versions add redis-connection-string --data-file=-

# admin-api-key protects POST /v1/admin/eigentrust. Generate a strong random value ONCE; never
# overwrite an existing one (that would rotate the key out from under any configured client).
if gcloud secrets describe admin-api-key >/dev/null 2>&1; then
  echo "  admin-api-key already exists, leaving it unchanged"
else
  openssl rand -base64 32 | tr -d '\n' | gcloud secrets create admin-api-key \
    --data-file=- \
    --replication-policy=automatic \
    --quiet
  echo "  Created admin-api-key (read it with: gcloud secrets versions access latest --secret=admin-api-key)"
fi

# -------------------------------------------------------
# 8. Service accounts: one per runtime, one for CI/CD
# -------------------------------------------------------
# The API and the batch job run as a dedicated runtime account that can reach Cloud SQL and read
# its own secrets, nothing else (the default compute account has Editor on the whole project).
# Staging gets its own, limited to the staging secrets. The CI/CD account deploys as them but
# cannot read any secret itself.
echo "[8/9] Creating service accounts..."
RUNTIME_SA="trustscore-runtime@${PROJECT_ID}.iam.gserviceaccount.com"
STAGING_RUNTIME_SA="trustscore-staging-runtime@${PROJECT_ID}.iam.gserviceaccount.com"
gcloud iam service-accounts create trustscore-runtime \
  --display-name="TrustScore API and batch job (production)" --quiet 2>/dev/null || echo "  (runtime SA exists)"
gcloud iam service-accounts create trustscore-staging-runtime \
  --display-name="TrustScore API and batch job (staging)" --quiet 2>/dev/null || echo "  (staging runtime SA exists)"
gcloud iam service-accounts create "$SA_NAME" \
  --display-name="GitHub Actions CI/CD" \
  --quiet 2>/dev/null || echo "  (CI/CD SA exists)"

for SA in "$RUNTIME_SA" "$STAGING_RUNTIME_SA"; do
  gcloud projects add-iam-policy-binding "$PROJECT_ID" \
    --member="serviceAccount:${SA}" --role=roles/cloudsql.client --condition=None --quiet > /dev/null
  # CI/CD may deploy workloads that run as this account, and only this one.
  gcloud iam service-accounts add-iam-policy-binding "$SA" \
    --member="serviceAccount:${SA_EMAIL}" --role=roles/iam.serviceAccountUser --quiet > /dev/null
done

for SECRET in db-connection-string redis-connection-string admin-api-key; do
  gcloud secrets add-iam-policy-binding "$SECRET" \
    --member="serviceAccount:${RUNTIME_SA}" --role=roles/secretmanager.secretAccessor --quiet > /dev/null
done
# Staging secrets are created by the runbook (docs/runbooks/deploy.md); bind them when present.
for SECRET in db-connection-string-staging redis-connection-string admin-api-key-staging; do
  if gcloud secrets describe "$SECRET" >/dev/null 2>&1; then
    gcloud secrets add-iam-policy-binding "$SECRET" \
      --member="serviceAccount:${STAGING_RUNTIME_SA}" --role=roles/secretmanager.secretAccessor --quiet > /dev/null
  fi
done

# CI/CD: deploy services and jobs, push images. No secretAccessor and no project-wide
# serviceAccountUser: it acts only as the two runtime accounts above.
for ROLE in \
  roles/run.admin \
  roles/artifactregistry.writer; do
  gcloud projects add-iam-policy-binding "$PROJECT_ID" \
    --member="serviceAccount:${SA_EMAIL}" \
    --role="$ROLE" \
    --quiet > /dev/null
done

# -------------------------------------------------------
# 9. Setup Workload Identity Federation (GitHub Actions → GCP, no keys)
# -------------------------------------------------------
echo "[9/9] Setting up Workload Identity Federation..."
POOL_NAME="github-pool"
PROVIDER_NAME="github-provider"
WIF_CONDITION="assertion.repository == 'trustscoreagent/trustscoreagent' && assertion.ref == 'refs/heads/main'"

gcloud iam workload-identity-pools create "$POOL_NAME" \
  --location="global" \
  --display-name="GitHub Actions Pool" \
  --quiet 2>/dev/null || echo "  (pool already exists)"

# An attribute-condition is REQUIRED by recent gcloud versions. It restricts the provider to this
# repository AND to its main branch: otherwise any workflow on any branch, run by anyone who can
# push one, could obtain the deploy account. Scheduled runs, workflow_run (staging deploys) and
# workflow_dispatch on main all present refs/heads/main.
gcloud iam workload-identity-pools providers create-oidc "$PROVIDER_NAME" \
  --location="global" \
  --workload-identity-pool="$POOL_NAME" \
  --display-name="GitHub Provider" \
  --attribute-mapping="google.subject=assertion.sub,attribute.repository=assertion.repository" \
  --attribute-condition="$WIF_CONDITION" \
  --issuer-uri="https://token.actions.githubusercontent.com" \
  --quiet 2>/dev/null || \
gcloud iam workload-identity-pools providers update-oidc "$PROVIDER_NAME" \
  --location="global" \
  --workload-identity-pool="$POOL_NAME" \
  --attribute-condition="$WIF_CONDITION" \
  --quiet

PROJECT_NUMBER=$(gcloud projects describe "$PROJECT_ID" --format='value(projectNumber)')
WIF_PROVIDER="projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_NAME}/providers/${PROVIDER_NAME}"

# Allow the GitHub repo to impersonate the service account
gcloud iam service-accounts add-iam-policy-binding "$SA_EMAIL" \
  --role="roles/iam.workloadIdentityUser" \
  --member="principalSet://iam.googleapis.com/projects/${PROJECT_NUMBER}/locations/global/workloadIdentityPools/${POOL_NAME}/attribute.repository/trustscoreagent/trustscoreagent" \
  --quiet > /dev/null

# -------------------------------------------------------
# Done — Print summary
# -------------------------------------------------------
echo ""
echo "=============================================="
echo "  SETUP COMPLETE"
echo "=============================================="
echo ""
echo "DB and Redis connection strings are stored in Secret Manager:"
echo "  db-connection-string, redis-connection-string"
echo "Retrieve with:"
echo "  gcloud secrets versions access latest --secret=db-connection-string"
echo ""
echo "=== GitHub Secrets to configure ==="
echo "Go to: https://github.com/trustscoreagent/trustscoreagent/settings/secrets/actions"
echo ""
echo "Add these 3 secrets:"
echo ""
echo "  GCP_PROJECT_ID"
echo "  → $PROJECT_ID"
echo ""
echo "  GCP_SERVICE_ACCOUNT"
echo "  → $SA_EMAIL"
echo ""
echo "  GCP_WORKLOAD_IDENTITY_PROVIDER"
echo "  → $WIF_PROVIDER"
echo ""
echo "The database password is held only inside the db-connection-string secret."
echo "It is intentionally not printed here; rotate it via 'gcloud sql users set-password'."
echo ""
