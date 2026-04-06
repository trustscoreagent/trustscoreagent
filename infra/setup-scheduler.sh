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
IMAGE="europe-west1-docker.pkg.dev/${PROJECT_ID}/trustscoreagent/api:latest"
SA_EMAIL="github-actions@${PROJECT_ID}.iam.gserviceaccount.com"

echo "=== TrustScoreAgent Scheduler Setup ==="

# 1. Enable Cloud Scheduler API
echo "[1/3] Enabling Cloud Scheduler API..."
gcloud services enable cloudscheduler.googleapis.com --quiet

# 2. Create Cloud Run Job
echo "[2/3] Creating Cloud Run Job..."
gcloud run jobs create "$JOB_NAME" \
  --image "$IMAGE" \
  --region "$REGION" \
  --args="--job" \
  --set-secrets "ConnectionStrings__PostgreSQL=db-connection-string:latest,ConnectionStrings__Redis=redis-connection-string:latest" \
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
  --set-secrets "ConnectionStrings__PostgreSQL=db-connection-string:latest,ConnectionStrings__Redis=redis-connection-string:latest" \
  --vpc-connector "trustscoreagent-connector" \
  --max-retries 2 \
  --task-timeout 300s \
  --cpu 1 \
  --memory 512Mi \
  --quiet

# 3. Create Cloud Scheduler trigger (every hour at minute 0)
echo "[3/3] Creating Cloud Scheduler trigger..."
gcloud scheduler jobs create http "$JOB_NAME-trigger" \
  --location "$REGION" \
  --schedule "0 * * * *" \
  --uri "https://${REGION}-run.googleapis.com/apis/run.googleapis.com/v1/namespaces/${PROJECT_ID}/jobs/${JOB_NAME}:run" \
  --http-method POST \
  --oauth-service-account-email "$SA_EMAIL" \
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
