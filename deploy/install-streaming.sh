#!/bin/bash
set -euo pipefail

fail() {
  echo "Installation failed: $*" >&2
  exit 1
}

trap 'echo "Installation failed at line $LINENO; the service has not been confirmed healthy." >&2' ERR

[[ $EUID -eq 0 ]] || fail "Run with sudo bash."
[[ "$(uname -s)" == Linux ]] || fail "Only Linux is supported."
for command in curl tar gzip sha256sum systemctl getconf df awk sort head mktemp openssl useradd ldd grep sed; do
  command -v "$command" > /dev/null || fail "Missing command: $command. Install it first."
done
[[ "$(uname -m)" == aarch64 && "$(getconf LONG_BIT)" == 64 ]] \
  || fail "A 64-bit ARM Linux OS is required (not x86 or 32-bit Raspberry Pi OS)."
[[ -d /run/systemd/system ]] || fail "systemd must be running as the system service manager."

echo "Choose the audio output:"
echo "  1) Sonos over Wi-Fi (default)"
echo "  2) Local audio output (ALSA: analog jack or USB DAC)"
while true; do
  if ! IFS= read -r -p "Output [1/2, default 1]: " playback_choice < /dev/tty; then
    fail "Could not read the audio output choice. Run from an interactive terminal."
  fi
  playback_choice="${playback_choice:-1}"
  case "$playback_choice" in
    1|sonos|SONOS) use_sonos=true; break ;;
    2|alsa|ALSA) use_sonos=false; break ;;
    *) echo "Choose 1 for Sonos or 2 for local ALSA output." >&2 ;;
  esac
done

memory_bytes=$(( $(getconf _PHYS_PAGES) * $(getconf PAGE_SIZE) ))
(( memory_bytes >= 512000000 )) || fail "At least 512 MB of RAM is required; detected $((memory_bytes / 1000000)) MB."
glibc="$(getconf GNU_LIBC_VERSION)" || fail "GNU libc is required; musl/Alpine is not supported."
[[ "$glibc" == glibc\ * ]] || fail "GNU libc is required."
echo "Detected Linux ARM64, $((memory_bytes / 1000000)) MB RAM, $glibc."

repository=https://github.com/egbertn/nieropstreamingserver
release_url="$(curl --proto '=https' --proto-redir '=https' -fsSL \
  --connect-timeout 15 --max-time 60 -o /dev/null -w '%{url_effective}' "$repository/releases/latest")" \
  || fail "No published release is available, or GitHub cannot be reached."
version="${release_url##*/}"
[[ "$release_url" == "$repository/releases/tag/$version" && "$version" =~ ^v[0-9][A-Za-z0-9._-]*$ ]] \
  || fail "Unexpected GitHub release URL: $release_url"

work_dir="$(mktemp -d /var/tmp/install-streaming.XXXXXXXX)"
trap 'rm -f "$work_dir/archive.tar.gz" "$work_dir/checksum"; rm -rf "$work_dir/package"; rmdir "$work_dir"' EXIT
archive="streaming-$version-linux-arm64.tar.gz"
download_url="$repository/releases/download/$version"
echo "Downloading $version..."
curl --proto '=https' --proto-redir '=https' -fsSL --connect-timeout 15 --max-time 600 \
  "$download_url/$archive" -o "$work_dir/archive.tar.gz"
curl --proto '=https' --proto-redir '=https' -fsSL --connect-timeout 15 --max-time 60 \
  "$download_url/$archive.sha256" -o "$work_dir/checksum"
read -r checksum checksum_file < "$work_dir/checksum"
[[ "$checksum" =~ ^[[:xdigit:]]{64}$ && "$checksum_file" == "$archive" ]] \
  || fail "Invalid release checksum file."
printf '%s  %s\n' "$checksum" "$work_dir/archive.tar.gz" | sha256sum --check --status \
  || fail "Download checksum does not match."

# Reserve space for both extraction and the installed copy, before modifying the service.
unpacked_bytes="$(gzip -l "$work_dir/archive.tar.gz" | awk 'NR == 2 {print $2}')"
required_kib=$(( (unpacked_bytes * 2 + 10485760 + 1023) / 1024 ))
for directory in /opt "$work_dir"; do
  available_kib="$(df -Pk "$directory" | awk 'NR == 2 {print $4}')"
  (( available_kib >= required_kib )) || fail "Not enough free space on $directory: need $required_kib KiB."
done

mkdir "$work_dir/package"
tar -xzf "$work_dir/archive.tar.gz" -C "$work_dir/package" --no-same-owner
package="$work_dir/package/streaming-$version-linux-arm64"
[[ -x "$package/streaming" && -f "$package/install.sh" && -f "$package/glibc-minimum" ]] \
  || fail "Incomplete release package; use a release that supports the bootstrap installer."
minimum_glibc="$(< "$package/glibc-minimum")"
[[ "$minimum_glibc" =~ ^[0-9]+\.[0-9]+$ ]] || fail "Invalid release libc requirement."
oldest="$(printf '%s\n%s\n' "$minimum_glibc" "${glibc#glibc }" | sort -V | head -n 1)"
[[ "$oldest" == "$minimum_glibc" ]] \
  || fail "This release needs glibc $minimum_glibc or newer; upgrade your OS."
dependencies="$(ldd "$package/streaming" 2>&1)" \
  || fail "Cannot check the executable's native libraries: $dependencies"
if [[ "$dependencies" == *"not found"* ]]; then
  fail "Required native libraries are missing. Install the matching OS packages:
$dependencies"
fi

if [[ "$use_sonos" == false ]] && ! command -v ffplay > /dev/null; then
  command -v apt-get > /dev/null || fail "ffplay is required for local ALSA playback, but apt-get is not available to install it."
  echo "ffplay was not found. Installing ffmpeg (which provides ffplay) with apt-get..."
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y ffmpeg
  command -v ffplay > /dev/null || fail "ffplay is still missing after installing ffmpeg."
fi

bash "$package/install.sh" /opt/streaming streaming --no-start
environment_file=/etc/kestrel-music/environment
if grep -q '^Streaming__Playback__UseSonos=' "$environment_file"; then
  sed -i "s|^Streaming__Playback__UseSonos=.*|Streaming__Playback__UseSonos=$use_sonos|" "$environment_file"
else
  printf 'Streaming__Playback__UseSonos=%s\n' "$use_sonos" >> "$environment_file"
fi
echo
echo "Installation complete. Start the service:"
if [[ "$use_sonos" == false ]]; then
  echo "  Local ALSA playback is selected; ffplay is available."
else
  echo "  Sonos over Wi-Fi is selected."
fi
echo "  sudo systemctl enable --now kestrel-music"
echo "Then open http://$(hostname).local:8080 (or this machine's LAN IP)."
