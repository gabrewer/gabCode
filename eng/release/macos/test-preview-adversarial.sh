#!/bin/bash
# Proves test-preview.sh rejects a structurally valid DMG with a stripped SwiftTerm notice.
set -euo pipefail

readonly expected_name='gabCode-0.0.1-preview.1-macos-arm64.dmg'
readonly script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
readonly verifier="$script_dir/test-preview.sh"

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

[[ $# -eq 1 ]] || fail "usage: $0 <path-to-${expected_name}>"
readonly source_dmg="$1"
[[ -f "$source_dmg" ]] || fail "DMG does not exist: $source_dmg"
[[ "$(basename "$source_dmg")" == "$expected_name" ]] || fail "expected filename ${expected_name}"
[[ -x "$verifier" ]] || fail "artifact verifier is not executable"

for command in hdiutil ditto; do
    command -v "$command" >/dev/null 2>&1 || fail "required command is unavailable: $command"
done

readonly attack_dir="$(mktemp -d "${TMPDIR:-/tmp}/gabcode-preview-adversarial.XXXXXX")"
readonly source_mount="$attack_dir/source-mount"
readonly stage_dir="$attack_dir/stage"
readonly tampered_dmg="$attack_dir/$expected_name"
mkdir "$source_mount" "$stage_dir"
attached=0
cleanup() {
    if [[ "$attached" -eq 1 ]]; then
        hdiutil detach "$source_mount" -quiet || hdiutil detach "$source_mount" -force -quiet || true
    fi
    rm -rf "$attack_dir"
}
trap cleanup EXIT HUP INT TERM

hdiutil attach -readonly -nobrowse -mountpoint "$source_mount" "$source_dmg" >/dev/null
attached=1
ditto --noqtn "$source_mount/" "$stage_dir/"
hdiutil detach "$source_mount" -quiet
attached=0

# Preserve the old marker lines so this regression defeats a marker-only implementation.
printf '%s\n' \
    'SwiftTerm' \
    'Version: 1.15.0' \
    'MIT License' \
    '' \
    'TAMPERED: required copyright and permission notice removed.' \
    > "$stage_dir/THIRD-PARTY-NOTICES.txt"
hdiutil create \
    -volname 'gabCode adversarial notice' \
    -srcfolder "$stage_dir" \
    -format UDZO \
    -ov \
    "$tampered_dmg" >/dev/null

if "$verifier" "$tampered_dmg"; then
    fail 'artifact verifier accepted a stripped SwiftTerm notice'
fi

printf 'Adversarial notice-strip rejection passed.\n'
