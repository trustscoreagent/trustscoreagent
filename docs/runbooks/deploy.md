# Deployment Runbook

## First-time setup

### 1. Run the GCP setup script

```bash
chmod +x infra/setup-gcp.sh
./infra/setup-gcp.sh
```

This creates: Cloud SQL, Redis, Artifact Registry, service account, Workload Identity Federation.

Takes ~10 minutes (Cloud SQL and Redis provisioning).

### 2. Configure GitHub Secrets

The script outputs 3 values. Add them to GitHub:
https://github.com/trustscoreagent/trustscoreagent/settings/secrets/actions

| Secret | Value |
|--------|-------|
| `GCP_PROJECT_ID` | `trustscoreagent` |
| `GCP_SERVICE_ACCOUNT` | `github-actions@trustscoreagent.iam.gserviceaccount.com` |
| `GCP_WORKLOAD_IDENTITY_PROVIDER` | (output by the script) |

### 3. Configure GitHub Environments

Go to: https://github.com/trustscoreagent/trustscoreagent/settings/environments

- Create **staging** (no protection rules)
- Create **production** (enable "Required reviewers" → add yourself)

## Deploying

### Staging (automatic)

Every push to `main` triggers a staging deploy automatically.

### Production (manual)

1. Go to Actions → "Deploy Production"
2. Click "Run workflow"
3. Enter the git ref (commit SHA or `main`)
4. Approve the deployment when prompted
5. The workflow deploys as canary (5% traffic), verifies for 5 min, then promotes to 100%

## Rollback

```bash
# List recent revisions
gcloud run revisions list --service trustscoreagent-api --region europe-west1

# Route all traffic to a specific revision
gcloud run services update-traffic trustscoreagent-api \
  --region europe-west1 \
  --to-revisions REVISION_NAME=100
```

Rollback takes ~2 seconds (traffic rerouting, no rebuild).
