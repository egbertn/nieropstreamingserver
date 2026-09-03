#!/bin/bash
set -euo pipefail

# Build output and deployment are configurable through environment variables.
# Example:
#   DEPLOY_TARGET=pi@raspberrypi.local TARGET_DIR=apps/music ./publish.sh

DEPLOY_TARGET="${DEPLOY_TARGET:-}"
[[ -n "$DEPLOY_TARGET" ]] || { echo "DEPLOY_TARGET is not set, for example: DEPLOY_TARGET=pi@raspberrypi.local" >&2; exit 1; }
TARGET_DIR="${TARGET_DIR:-apps/music}"
SERVICE_NAME="${SERVICE_NAME:-kestrel-music}"
PUBLISH_RID="${PUBLISH_RID:-linux-arm64}"

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
OUTDIR="$PROJECT_DIR/out/publish"
SERVICE_FILE="${SERVICE_FILE:-$PROJECT_DIR/deploy/kestrel-music.service}"

REMOTE_USER="${DEPLOY_TARGET%%@*}"

echo "Publishing to $OUTDIR"
dotnet publish "$PROJECT_DIR/streaming.csproj" -r "$PUBLISH_RID" -c Release --self-contained false -o "$OUTDIR"

if [[ ! -d "$OUTDIR" ]] || [[ -z "$(find "$OUTDIR" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo "Publish output is missing in $OUTDIR"
  exit 1
fi

echo "Stopping $SERVICE_NAME on $DEPLOY_TARGET"
ssh "$DEPLOY_TARGET" "sudo systemctl stop $SERVICE_NAME || true"

echo "Creating target directory"
ssh "$DEPLOY_TARGET" "mkdir -p \"\$HOME/$TARGET_DIR\""

# The music collection lives on the Pi itself and must not be wiped by --delete.
echo "Syncing files"
rsync -az --delete --exclude 'wwwroot/music/***' "$OUTDIR/" "$DEPLOY_TARGET:$TARGET_DIR/"

if [[ -f "$SERVICE_FILE" ]]; then
  echo "Installing systemd service"
  sed -e "s#__USER__#$REMOTE_USER#g" -e "s#__DIR__#/home/$REMOTE_USER/$TARGET_DIR#g" "$SERVICE_FILE" \
    | ssh "$DEPLOY_TARGET" "sudo tee /etc/systemd/system/$SERVICE_NAME.service > /dev/null"
  ssh "$DEPLOY_TARGET" "sudo systemctl daemon-reload && sudo systemctl enable $SERVICE_NAME"
else
  echo "Service file not found at $SERVICE_FILE, skipping systemd install"
fi

echo "Starting $SERVICE_NAME"
ssh "$DEPLOY_TARGET" "sudo systemctl start $SERVICE_NAME && sudo systemctl status $SERVICE_NAME --no-pager"
