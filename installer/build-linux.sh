#!/bin/sh
set -eu
rid="${1:-linux-x64}"
case "$rid" in linux-x64|linux-arm64) ;; *) echo "Expected linux-x64 or linux-arm64" >&2; exit 2 ;; esac
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
# Unique staging directory: never remove or overwrite an existing publish directory.
mkdir -p "$root/artifacts"
stage=$(mktemp -d "$root/artifacts/linux.XXXXXXXX")
trap 'echo "Build staging retained: $stage"' EXIT
dotnet publish "$root/src/Spacewise.Linux" -c Release -r "$rid" --self-contained -o "$stage/Spacewise"
chmod u+x "$stage/Spacewise/Spacewise.Linux"
cp "$root/docs/LINUX.md" "$stage/Spacewise/README.md"
archive="$stage/Spacewise-$rid.tar.gz"
tar -czf "$archive" -C "$stage" Spacewise
sha256sum "$archive"
printf 'Archive: %s\n' "$archive"
