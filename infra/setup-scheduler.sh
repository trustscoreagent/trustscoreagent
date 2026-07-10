#!/bin/bash
# ============================================================================
# TrustScoreAgent — Cloud Scheduler + Cloud Run Job Setup
# ============================================================================
# Creates an hourly job that runs EigenTrust + Merkle anchoring.
# Prerequisites: GCP setup done (setup-gcp.sh), API deployed on Cloud Run.
#
# Usage:
#   chmod +x infra/setup-scheduler.sh
#   ./infra/setup-scheduler.sh
# ============================================================================

set -euo pipefail

PROJECT_ID="trustscoreagent"
REGION="europe-west1"
JOB_NAME="trustscoreagent-hourly"
API_SERVICE="trustscoreagent-api"
# Dedicated, least-privilege identity for the scheduler — only allowed to run THIS job,
# instead of reusing the broad github-actions CI/CD service account.
SCHEDULER_SA_NAME="scheduler-invoker"
SCHEDULER_SA_EMAIL="${SCHEDULER_SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

gcloud config set project "$PROJECT_ID" >/dev/null 2>&1 || true

echo "=== TrustScoreAgent Scheduler Setup ==="

# Run the SAME image the production API currently serves, so the hourly job runs identical code.
# The deploy pipeline tags images by commit SHA (there is no :latest tag), so resolve it live.
IMAGE=$(gcloud run services describe "$API_SERVICE" \
  --region "$REGION" --project "$PROJECT_ID" \
  --format 'value(spec.template.spec.containers[0].image)' 2>/dev/null || true)
if [ -z "$IMAGE" ]; then
  echo "ERROR: could not resolve the '$API_SERVICE' image. Deploy the API to Cloud Run first." >&2
  exit 1
fi
echo "  Using production image: $IMAGE"

# 1. Enable Cloud Scheduler API
echo "[1/4] Enabling Cloud Scheduler API..."
gcloud services enable cloudscheduler.googleapis.com --quiet

# 2. Create Cloud Run Job
echo "[2/4] Creating Cloud Run Job..."
gcloud run jobs create "$JOB_NAME" \
  --image "$IMAGE" \
  --region "$REGION" \
  --args="--job" \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Production" \
  --set-secrets "ConnectionStrings__PostgreSQL=db-connection-string:latest,ConnectionStrings__Redis=redis-connection-string:latest" \
  --set-cloudsql-instances "${PROJECT_ID}:${REGION}:trustscoreagent-db" \
  --vpc-connector "trustscoreagent-connector" \
  --max-retries 2 \
  --task-timeout 300s \
  --cpu 1 \
  --memory 512Mi \
  --quiet 2>/dev/null || \
gcloud run jobs update "$JOB_NAME" \
  --image "$IMAGE" \
  --region "$REGION" \
  --args="--job" \
  --set-env-vars "ASPNETCORE_ENVIRONMENT=Production" \
  --set-secrets "ConnectionStrings__PostgreSQL=db-connection-string:latest,ConnectionStrings__Redis=redis-connection-string:latest" \
  --set-cloudsql-instances "${PROJECT_ID}:${REGION}:trustscoreagent-db" \
  --vpc-connector "trustscoreagent-connector" \
  --max-retries 2 \
  --task-timeout 300s \
  --cpu 1 \
  --memory 512Mi \
  --quiet

# 3. Create the dedicated scheduler identity and let it run ONLY this job.
echo "[3/4] Creating scheduler service account..."
gcloud iam service-accounts create "$SCHEDULER_SA_NAME" \
  --display-name="Cloud Scheduler — hourly job invoker" \
  --quiet 2>/dev/null || echo "  (already exists)"

gcloud run jobs add-iam-policy-binding "$JOB_NAME" \
  --region "$REGION" \
  --member="serviceAccount:${SCHEDULER_SA_EMAIL}" \
  --role="roles/run.invoker" \
  --quiet > /dev/null

# 4. Create Cloud Scheduler trigger (every hour at minute 0)
echo "[4/4] Creating Cloud Scheduler trigger..."
gcloud scheduler jobs create http "$JOB_NAME-trigger" \
  --location "$REGION" \
  --schedule "0 * * * *" \
  --uri "https://${REGION}-run.googleapis.com/apis/run.googleapis.com/v1/namespaces/${PROJECT_ID}/jobs/${JOB_NAME}:run" \
  --http-method POST \
  --oauth-service-account-email "$SCHEDULER_SA_EMAIL" \
  --quiet 2>/dev/null || \
echo "  (scheduler already exists)"

echo ""
echo "=== Setup Complete ==="
echo ""
echo "Job: $JOB_NAME (runs EigenTrust + Merkle anchoring)"
echo "Schedule: every hour at minute 0"
echo ""
echo "Manual test:"
echo "  gcloud run jobs execute $JOB_NAME --region $REGION"
echo ""
echo "View logs:"
echo "  gcloud run jobs executions list --job $JOB_NAME --region $REGION"
