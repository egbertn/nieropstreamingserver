#!/bin/bash
# Internal helper invoked by install-streaming.sh from an extracted release package.
# The public installer performs the prerequisite checks and audio configuration.
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "The installation helper must run as root; use the public install-streaming.sh installer with sudo." >&2
  exit 1
fi

INSTALL_DIR="${1:-/opt/streaming}"
SERVICE_USER="${2:-streaming}"
START_SERVICE="${3:-}"
SERVICE_NAME=nierop-music
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ -z "$SERVICE_USER" || "$SERVICE_USER" == root ]]; then
  echo "The installation helper requires a non-root service user." >&2
  exit 1
fi
[[ "$INSTALL_DIR" =~ ^/[A-Za-z0-9_./-]+$ && "$INSTALL_DIR" != / ]] || {
  echo "Use an absolute installation directory without spaces or special characters." >&2
  exit 1
}
INSTALL_DIR="$(realpath -m -- "$INSTALL_DIR")"
case "$INSTALL_DIR" in
  /opt/*|/srv/*|/home/*/*) ;;
  *)
    echo "Choose an application subdirectory under /opt, /srv or a user's home." >&2
    exit 1
    ;;
esac
[[ -z "$START_SERVICE" || "$START_SERVICE" == --no-start ]] || {
  echo "The only supported third argument is --no-start." >&2
  exit 1
}
for command in curl openssl systemctl; do
  command -v "$command" > /dev/null || {
    echo "Required command not found: $command" >&2
    exit 1
  }
done
if [[ "$SERVICE_USER" == streaming ]] && ! id streaming > /dev/null 2>&1; then
  useradd --system --user-group --home-dir "$INSTALL_DIR" --shell /usr/sbin/nologin streaming
fi
id "$SERVICE_USER" > /dev/null
SERVICE_GROUP="$(id -gn "$SERVICE_USER")"

if [[ ! -x "$SRC_DIR/streaming" ]]; then
  echo "The 'streaming' binary was not found next to install.sh." >&2
  echo "The release package is incomplete; run the public install-streaming.sh installer." >&2
  exit 1
fi
[[ "$SRC_DIR" != "$INSTALL_DIR" ]] || {
  echo "Extract the release outside the installation directory." >&2
  exit 1
}
host="$(hostname)"
[[ "$host" =~ ^[A-Za-z0-9][A-Za-z0-9.-]*$ ]] || {
  echo "Hostname is not suitable for a LAN URL: $host" >&2
  exit 1
}
token=""
if [[ ! -f /etc/nierop-music/environment ]]; then
  token="$(openssl rand -hex 24)"
fi

if systemctl is-active --quiet "$SERVICE_NAME"; then
  systemctl stop "$SERVICE_NAME"
fi

echo "Installing to $INSTALL_DIR (service user: $SERVICE_USER)"
mkdir -p "$INSTALL_DIR"

# Copy the application; never touch the music collection or the database.
shopt -s dotglob
shopt -s nullglob
for entry in "$SRC_DIR"/*; do
  base="$(basename "$entry")"
  case "$base" in
    install.sh|nierop-music.service|glibc-minimum|data) continue ;;
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
shopt -u nullglob

mkdir -p "$INSTALL_DIR/wwwroot/music" "$INSTALL_DIR/data"
chown -R "$SERVICE_USER:$SERVICE_GROUP" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/streaming"

install -d -m 700 /etc/nierop-music
if [[ ! -f /etc/nierop-music/environment ]]; then
  (umask 077; cat > /etc/nierop-music/environment <<EOF
Streaming__ApiToken=$token
Streaming__PublicBaseUrl=http://$host.local:8080
Streaming__Sonos__Addresses=
EOF
  )
fi

sed -e "s|__DIR__|$INSTALL_DIR|g" -e "s|__USER__|$SERVICE_USER|g" \
  -e "s|^Group=.*|Group=$SERVICE_GROUP|" \
  "$SRC_DIR/nierop-music.service" > "/etc/systemd/system/$SERVICE_NAME.service"

systemctl daemon-reload
if [[ "$START_SERVICE" == --no-start ]]; then
  echo "Service installed but not started."
  echo "Start it with: sudo systemctl enable --now $SERVICE_NAME"
  echo "API token (enter this in the browser):"
  grep '^Streaming__ApiToken=' /etc/nierop-music/environment
  exit 0
fi
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

echo "Waiting for the service to come up..."
healthy=false
for _ in $(seq 1 10); do
  if curl -sf --max-time 3 http://localhost:8080/health > /dev/null; then
    healthy=true
    break
  fi
  sleep 2
done

if [[ "$healthy" == true ]]; then
  echo "Service is healthy."
else
  echo "Service failed its health check (default port 8080). Check your port overrides and:" >&2
  echo "  journalctl -u $SERVICE_NAME -f" >&2
  exit 1
fi

cat <<EOF

Installed. Next steps:
  1. Open http://$(hostname).local:8080 from a browser on the same network.
  2. View the generated API token (enter this in the browser):
       sudo cat /etc/nierop-music/environment
  3. Edit that file to change the token, speaker addresses or LAN URL,
     then run sudo systemctl restart $SERVICE_NAME.
  4. Open the firewall for the speaker (see README, section Firewall).
  5. Upload music from the control page, or copy files into
     $INSTALL_DIR/wwwroot/music
EOF
