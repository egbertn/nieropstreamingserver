#!/bin/bash
# Installs the streaming service from an extracted release package.
# Run on the target machine (e.g. a Raspberry Pi) from the extracted directory:
#
#   sudo ./install.sh [install-dir] [service-user]
#
# Defaults: install-dir=/opt/streaming, service-user=the user invoking sudo.
# Safe to re-run for upgrades: the music collection and database are kept.
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "Run this script with sudo: sudo ./install.sh" >&2
  exit 1
fi

INSTALL_DIR="${1:-/opt/streaming}"
SERVICE_USER="${2:-${SUDO_USER:-}}"
SERVICE_NAME=kestrel-music
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ -z "$SERVICE_USER" || "$SERVICE_USER" == root ]]; then
  echo "Pass the non-root user that should run the service:" >&2
  echo "  sudo ./install.sh [install-dir] <user>" >&2
  exit 1
fi
id "$SERVICE_USER" > /dev/null

if [[ ! -x "$SRC_DIR/streaming" ]]; then
  echo "The 'streaming' binary was not found next to install.sh." >&2
  echo "Run this script from the extracted release directory." >&2
  exit 1
fi

echo "Installing to $INSTALL_DIR (service user: $SERVICE_USER)"
mkdir -p "$INSTALL_DIR"

# Copy the application; never touch the music collection or the database.
shopt -s dotglob
for entry in "$SRC_DIR"/*; do
  base="$(basename "$entry")"
  case "$base" in
    install.sh|kestrel-music.service|data) continue ;;
    wwwroot)
      mkdir -p "$INSTALL_DIR/wwwroot"
      for asset in "$entry"/*; do
        [[ "$(basename "$asset")" == music ]] && continue
        cp -r "$asset" "$INSTALL_DIR/wwwroot/"
      done
      ;;
    *) cp -r "$entry" "$INSTALL_DIR/" ;;
  esac
done
shopt -u dotglob

mkdir -p "$INSTALL_DIR/wwwroot/music" "$INSTALL_DIR/data"
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/streaming"

sed -e "s|__DIR__|$INSTALL_DIR|g" -e "s|__USER__|$SERVICE_USER|g" \
  "$SRC_DIR/kestrel-music.service" > "/etc/systemd/system/$SERVICE_NAME.service"

systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"

echo "Waiting for the service to come up..."
healthy=false
for _ in $(seq 1 10); do
  if command -v curl > /dev/null && curl -sf http://localhost:8080/health > /dev/null; then
    healthy=true
    break
  fi
  sleep 2
done

if [[ "$healthy" == true ]]; then
  echo "Service is healthy."
else
  echo "Could not confirm health automatically; check with:"
  echo "  systemctl status $SERVICE_NAME"
  echo "  journalctl -u $SERVICE_NAME -f"
fi

cat <<EOF

Installed. Next steps:
  1. Open http://$(hostname).local:8080 from a browser on the same network.
  2. Protect the API (recommended):
       sudo systemctl edit $SERVICE_NAME
     and add:
       [Service]
       Environment=Streaming__ApiToken=\$(openssl rand -base64 24)
  3. If this machine is not reachable as raspberrypi.local, set:
       Environment=Streaming__PublicBaseUrl=http://<this-host>:8080
  4. Open the firewall for the speaker (see README, section Firewall).
  5. Upload music from the control page, or copy files into
     $INSTALL_DIR/wwwroot/music
EOF
