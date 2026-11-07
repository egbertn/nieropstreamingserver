#!/bin/bash
set -euo pipefail

repository="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT
trap 'echo "Test failed at line $LINENO" >&2; if [[ -f "$test_root/output" ]]; then cat "$test_root/output" >&2; fi' ERR
export TEST_ROOT="$test_root"
mkdir -p "$test_root/run/systemd/system" "$test_root/fixture/streaming-v1.2.3-linux-arm64"
package="$test_root/fixture/streaming-v1.2.3-linux-arm64"
printf '#!/bin/bash\nprintf "%%s\\n" "$*" > "$TEST_ROOT/invocation"\nmkdir -p "$TEST_ROOT/etc/kestrel-music"\nprintf "Streaming__ApiToken=%%048d\\n" 0 > "$TEST_ROOT/etc/kestrel-music/environment"\n' > "$package/install.sh"
printf '#!/bin/bash\nexit 0\n' > "$package/streaming"
chmod +x "$package/streaming"
echo 2.36 > "$package/glibc-minimum"
tar -czf "$test_root/release.tar.gz" -C "$test_root/fixture" streaming-v1.2.3-linux-arm64

# Only bypass root and systemd-host gates in the fixture; no real service is modified.
sed -e '/^\[\[ \$EUID -eq 0 \]\]/d' \
  -e "s|/run/systemd/system|$test_root/run/systemd/system|" \
  -e "s|/dev/tty|$test_root/tty|" \
  -e "s|/etc/kestrel-music/environment|$test_root/etc/kestrel-music/environment|g" \
  "$repository/deploy/install-streaming.sh" > "$test_root/bootstrap.sh"
echo 1 > "$test_root/tty"

uname() {
  case "$1" in -s) echo "${TEST_OS:-Linux}" ;; -m) echo "${TEST_ARCH:-aarch64}" ;; esac
}
getconf() {
  case "$1" in
    LONG_BIT) echo "${TEST_BITS:-64}" ;;
    _PHYS_PAGES) echo "${TEST_PAGES:-125000}" ;;
    PAGE_SIZE) echo 4096 ;;
    GNU_LIBC_VERSION) echo "glibc ${TEST_GLIBC:-2.36}" ;;
  esac
}
df() {
  printf 'Filesystem 1024-blocks Used Available Capacity Mounted on\n'
  printf 'test 9999999 0 %s 0%% /\n' "${TEST_DISK:-9999999}"
}
ldd() { echo "${TEST_LIBRARIES:-libc.so.6 => /lib/libc.so.6}"; }
curl() {
  local url="" output="" previous=""
  for argument in "$@"; do
    if [[ "$previous" == -o ]]; then output="$argument"; fi
    if [[ "$argument" == http://* || "$argument" == https://* ]]; then url="$argument"; fi
    previous="$argument"
  done
  case "$url" in
    http://localhost:8080/health) [[ "${TEST_HEALTH:-true}" == true ]] ;;
    */releases/latest)
      [[ "${TEST_NO_RELEASE:-false}" == false ]] || return 22
      echo "https://github.com/egbertn/nieropstreamingserver/releases/tag/v1.2.3"
      ;;
    *.sha256)
      if [[ "${TEST_BAD_CHECKSUM:-false}" == true ]]; then
        printf '%064d  streaming-v1.2.3-linux-arm64.tar.gz\n' 0 > "$output"
      else
        printf '%s  streaming-v1.2.3-linux-arm64.tar.gz\n' \
          "$(sha256sum "$TEST_ROOT/release.tar.gz" | cut -d ' ' -f 1)" > "$output"
      fi
      ;;
    *.tar.gz) cp "$TEST_ROOT/release.tar.gz" "$output" ;;
    *) return 1 ;;
  esac
}
useradd() { echo "useradd $*" >> "$TEST_ROOT/system.log"; }
command() {
  if [[ "${1:-}" == -v && "${2:-}" == ffplay ]]; then
    [[ "${TEST_FFPLAY:-false}" == true ]]
    return
  fi
  builtin command "$@"
}
apt-get() {
  echo "apt-get $*" >> "$TEST_ROOT/apt.log"
  TEST_FFPLAY=true
}
systemctl() {
  echo "$*" >> "$TEST_ROOT/system.log"
  if [[ "$1" == is-active ]]; then [[ "${TEST_ACTIVE:-false}" == true ]]; fi
}
export -f uname getconf df ldd curl useradd command apt-get systemctl

expect_failure() {
  local name="$1" message="$2"
  shift 2
  rm -f "$test_root/invocation"
  if env "$@" bash "$test_root/bootstrap.sh" > "$test_root/output" 2>&1; then
    echo "FAIL: $name unexpectedly succeeded" >&2
    exit 1
  fi
  grep -Fq "$message" "$test_root/output"
  [[ ! -f "$test_root/invocation" ]]
  echo "PASS: $name"
}

expect_failure "unsupported OS" "Only Linux" TEST_OS=Darwin
expect_failure "x86" "64-bit ARM" TEST_ARCH=x86_64
expect_failure "32-bit userspace" "64-bit ARM" TEST_BITS=32
expect_failure "below 512 MB" "At least 512 MB" TEST_PAGES=124999
expect_failure "old glibc" "needs glibc 2.36" TEST_GLIBC=2.35
expect_failure "missing library" "libraries are missing" TEST_LIBRARIES="libz.so.1 => not found"
expect_failure "no stable release" "No published release" TEST_NO_RELEASE=true
expect_failure "checksum mismatch" "checksum does not match" TEST_BAD_CHECKSUM=true
expect_failure "insufficient disk" "Not enough free space" TEST_DISK=1
rmdir "$test_root/run/systemd/system"
expect_failure "no running systemd" "systemd must be running"
mkdir "$test_root/run/systemd/system"

bash "$test_root/bootstrap.sh" > "$test_root/output" 2>&1
grep -Fxq '/opt/streaming streaming --no-start' "$test_root/invocation"
grep -Fxq 'Streaming__Playback__UseSonos=true' "$test_root/etc/kestrel-music/environment"
echo "PASS: exactly 512 MB, verified download and no-start installation"

echo 2 > "$test_root/tty"
env TEST_FFPLAY=false bash "$test_root/bootstrap.sh" > "$test_root/output" 2>&1
grep -Fxq 'apt-get update' "$test_root/apt.log"
grep -Fxq 'apt-get install -y ffmpeg' "$test_root/apt.log"
grep -Fxq 'Streaming__Playback__UseSonos=false' "$test_root/etc/kestrel-music/environment"
grep -q '^Streaming__ApiToken=[a-f0-9]\{48\}$' "$test_root/etc/kestrel-music/environment"
echo "PASS: ALSA selection installs ffmpeg when ffplay is missing and preserves environment"

echo 1 > "$test_root/tty"
env TEST_FFPLAY=false bash "$test_root/bootstrap.sh" > "$test_root/output" 2>&1
grep -Fxq 'Streaming__Playback__UseSonos=true' "$test_root/etc/kestrel-music/environment"
[[ "$(wc -l < "$test_root/apt.log")" == 2 ]]
echo "PASS: Sonos selection restores Sonos mode without installing ffmpeg"

env TEST_GLIBC=2.40 bash "$test_root/bootstrap.sh" > "$test_root/output" 2>&1
echo "PASS: newer glibc"

# Exercise the real package copy/configuration logic using private filesystem paths.
mkdir -p "$test_root/opt" "$test_root/etc/systemd/system" "$test_root/source/wwwroot/music"
rm -f "$test_root/etc/kestrel-music/environment"
sed -e 's/if \[\[ \$EUID -ne 0 \]\]; then/if false; then/' \
  -e "s|/etc/|$test_root/etc/|g" -e "s|/opt/|$test_root/opt/|g" \
  "$repository/deploy/install.sh" > "$test_root/source/install.sh"
cp "$repository/deploy/kestrel-music.service" "$test_root/source/kestrel-music.service"
cp "$package/streaming" "$test_root/source/streaming"
echo '{}' > "$test_root/source/appsettings.json"
echo new > "$test_root/source/wwwroot/index.html"
echo excluded > "$test_root/source/wwwroot/music/excluded.mp3"
id() { if [[ "$1" == -gn ]]; then echo streaming; else return 0; fi; }
chown() { :; }
hostname() { echo raspberrypi; }
sleep() { :; }
export -f id chown hostname sleep

bash "$test_root/source/install.sh" "$test_root/opt/streaming" streaming --no-start > "$test_root/output" 2>&1
[[ -f "$test_root/opt/streaming/wwwroot/index.html" ]]
[[ ! -f "$test_root/opt/streaming/wwwroot/music/excluded.mp3" ]]
grep -q '^Streaming__ApiToken=[a-f0-9]\{48\}$' "$test_root/etc/kestrel-music/environment"
grep -Fxq 'Streaming__PublicBaseUrl=http://raspberrypi.local:8080' "$test_root/etc/kestrel-music/environment"
[[ "$(stat -c %a "$test_root/etc/kestrel-music/environment")" == 600 ]]
! grep -q '^restart' "$test_root/system.log"
echo "PASS: first install, generated protected token, LAN URL, no service start"

echo music > "$test_root/opt/streaming/wwwroot/music/existing.mp3"
echo database > "$test_root/opt/streaming/data/streaming.db"
echo schedule > "$test_root/opt/streaming/data/schedule.json"
echo custom > "$test_root/opt/streaming/appsettings.json"
cp "$test_root/etc/kestrel-music/environment" "$test_root/environment-before"
env TEST_ACTIVE=true bash "$test_root/source/install.sh" "$test_root/opt/streaming" streaming --no-start \
  > "$test_root/output" 2>&1
grep -Fxq music "$test_root/opt/streaming/wwwroot/music/existing.mp3"
grep -Fxq database "$test_root/opt/streaming/data/streaming.db"
grep -Fxq schedule "$test_root/opt/streaming/data/schedule.json"
grep -Fxq custom "$test_root/opt/streaming/appsettings.json"
cmp "$test_root/environment-before" "$test_root/etc/kestrel-music/environment"
grep -Fxq 'stop kestrel-music' "$test_root/system.log"
grep -Fxq 'restart kestrel-music' "$test_root/system.log"
echo "PASS: upgrade preserves music, database, schedule, settings and token; restarts service"

if env TEST_HEALTH=false bash "$test_root/source/install.sh" "$test_root/opt/streaming" streaming \
  > "$test_root/output" 2>&1; then
  echo "FAIL: unhealthy service reported success" >&2
  exit 1
fi
grep -Fq "Service failed its health check" "$test_root/output"
echo "PASS: unhealthy service fails explicitly"
