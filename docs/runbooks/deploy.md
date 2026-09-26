# Deployment Runbook

## First-time setup

### 1. Run the GCP setup script

```bash
chmod +x infra/setup-gcp.sh
./infra/setup-gcp.sh
```

This creates: Cloud SQL, Artifact Registry, secrets, service account, Workload Identity
Federation. Redis is an external Upstash database: export its connection string as
`REDIS_CONNECTION_STRING` before running the script.

Takes ~10 minutes (Cloud SQL and Redis provisioning).

### 2. Configure GitHub Secrets

The script outputs 3 values. Add them to GitHub:
https://github.com/trustscoreagent/trustscoreagent/settings/secrets/actions

| Secret | Value |
|--------|-------|
| `GCP_PROJECT_ID` | `trustscoreagent` |
| `GCP_SERVICE_ACCOUNT` | `github-actions@trustscoreagent.iam.gserviceaccount.com` |
| `GCP_WORKLOAD_IDENTITY_PROVIDER` | (output by the script) |

### 3. Give staging its own database

Staging must not share production data: a migration merged to `main` would otherwise reach
production as soon as staging deploys, before the production approval gate.

```bash
gcloud sql databases create trustscore_staging --instance trustscoreagent-db
# Same credentials as production, different database; never printed.
gcloud secrets versions access latest --secret db-connection-string \
  | sed 's/Database=trustscore;/Database=trustscore_staging;/' \
  | gcloud secrets create db-connection-string-staging --replication-policy automatic --data-file=-
```

The staging deploy reads `db-connection-string-staging` and sets `Redis__KeyPrefix=staging:`,
so it shares the Redis instance without sharing keys.

### 4. Configure GitHub Environments

Go to: https://github.com/trustscoreagent/trustscoreagent/settings/environments

- Create **staging** (no protection rules)
- Create **production** (enable "Required reviewers" and add yourself)

## Deploying

### Staging (automatic)

Every push to `main` deploys to staging once CI has passed on it.

### Production (manual)

1. Go to Actions, then "Deploy Production"
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
