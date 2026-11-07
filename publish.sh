#!/bin/bash
set -euo pipefail

command -v gh > /dev/null || {
  echo "GitHub CLI (gh) is required to start the deployment workflow." >&2
  exit 1
}

REF="${REF:-main}"
REPOSITORY="${REPOSITORY:-egbertn/nieropstreamingserver}"

gh workflow run deploy.yml --repo "$REPOSITORY" --ref "$REF"
echo "Deployment requested for $REPOSITORY at $REF."
gh run list --repo "$REPOSITORY" --workflow deploy.yml --limit 1
