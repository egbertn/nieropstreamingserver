#!/bin/bash
set -euo pipefail

source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
destination=/home/moeder/apps/nieropcomputervision.com/downloads
url=https://nieropcomputervision.com/install-streaming.sh

test -d /home/moeder/apps/nieropcomputervision.com
bash -n "$source_dir/install-streaming.sh"
mkdir -p "$destination"
temporary="$(mktemp "$destination/.install-streaming.XXXXXXXX")"
download="$(mktemp)"
trap 'rm -f "$temporary" "$download"' EXIT
install -m 644 "$source_dir/install-streaming.sh" "$temporary"
mv -f "$temporary" "$destination/install-streaming.sh"
curl --proto '=https' --proto-redir '=https' -fsSL \
  --connect-timeout 15 --max-time 60 \
  -H 'Cache-Control: no-cache' "$url" -o "$download"
if ! cmp -s "$source_dir/install-streaming.sh" "$download"; then
  echo "Published installer does not match the file returned by $url." >&2
  exit 1
fi
echo "Installer published and verified: $url"
