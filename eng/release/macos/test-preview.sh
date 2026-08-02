#!/bin/bash
# Verifies one gabCode unsigned-preview DMG without modifying it or its contents.
set -euo pipefail

[[ $# -eq 1 ]] || { printf 'ERROR: usage: %s <path-to-gabCode-x.y.z-preview.n-macos-arm64.dmg>\n' "$0" >&2; exit 1; }
readonly dmg_path="$1"
readonly expected_name="$(basename "$dmg_path")"
if [[ ! "$expected_name" =~ ^gabCode-([0-9]+\.[0-9]+\.[0-9]+)-preview\.([1-9][0-9]*)-macos-arm64\.dmg$ ]]; then
    printf 'ERROR: expected a versioned gabCode macOS preview DMG\n' >&2
    exit 1
fi
readonly expected_marketing_version="${BASH_REMATCH[1]}"
readonly expected_build_number="${BASH_REMATCH[2]}"
readonly expected_bundle_identifier='com.gabrewer.gabcode'
readonly script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
readonly repo_root="$(cd "$script_dir/../../.." && pwd -P)"
readonly expected_license="$repo_root/LICENSE"
readonly expected_third_party_notices="$script_dir/THIRD-PARTY-NOTICES.txt"

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "required command is unavailable: $1"
}

[[ -f "$dmg_path" ]] || fail "DMG does not exist: $dmg_path"
[[ -f "$expected_license" && ! -L "$expected_license" ]] || fail "reviewed gabCode license is missing"
[[ -f "$expected_third_party_notices" && ! -L "$expected_third_party_notices" ]] || fail "reviewed SwiftTerm notice is missing"

for command in hdiutil codesign plutil lipo file shasum cmp; do
    require_command "$command"
done

hdiutil verify "$dmg_path"

readonly mount_dir="$(mktemp -d "${TMPDIR:-/tmp}/gabcode-preview-verify.XXXXXX")"
attached=0
cleanup() {
    if [[ "$attached" -eq 1 ]]; then
        hdiutil detach "$mount_dir" -quiet || hdiutil detach "$mount_dir" -force -quiet || true
    fi
    rmdir "$mount_dir" 2>/dev/null || true
}
trap cleanup EXIT HUP INT TERM

hdiutil attach -readonly -nobrowse -mountpoint "$mount_dir" "$dmg_path" >/dev/null
attached=1
[[ -d "$mount_dir/gabCode.app" ]] || fail "DMG does not contain gabCode.app"
[[ -L "$mount_dir/Applications" ]] || fail "DMG does not contain an Applications drag target"
[[ "$(readlink "$mount_dir/Applications")" == '/Applications' ]] || fail "Applications target must point to /Applications"
[[ -f "$mount_dir/LICENSE.txt" ]] || fail "DMG does not contain gabCode MIT license"
[[ -f "$mount_dir/THIRD-PARTY-NOTICES.txt" ]] || fail "DMG does not contain SwiftTerm notice"

expected_root_entries=$'Applications\nLICENSE.txt\nTHIRD-PARTY-NOTICES.txt\ngabCode.app'
actual_root_entries="$(find "$mount_dir" -mindepth 1 -maxdepth 1 -exec basename {} \; | LC_ALL=C sort)"
[[ "$actual_root_entries" == "$expected_root_entries" ]] || fail "DMG root contains unexpected or missing files: ${actual_root_entries}"

[[ ! -L "$mount_dir/LICENSE.txt" ]] || fail "gabCode license must not be a symlink"
[[ ! -L "$mount_dir/THIRD-PARTY-NOTICES.txt" ]] || fail "SwiftTerm notice must not be a symlink"
cmp -s "$expected_license" "$mount_dir/LICENSE.txt" || fail "packaged gabCode license differs from the reviewed source"
cmp -s "$expected_third_party_notices" "$mount_dir/THIRD-PARTY-NOTICES.txt" || fail "packaged SwiftTerm notice differs from the reviewed pinned source"

readonly app_path="$mount_dir/gabCode.app"
readonly executable_path="$app_path/Contents/MacOS/gabCode"
[[ -f "$executable_path" ]] || fail "app executable is missing"
[[ "$(plutil -extract CFBundleIdentifier raw -expect string "$app_path/Contents/Info.plist")" == "$expected_bundle_identifier" ]] || fail "unexpected bundle identifier"
[[ "$(plutil -extract CFBundleShortVersionString raw -expect string "$app_path/Contents/Info.plist")" == "$expected_marketing_version" ]] || fail "unexpected marketing version"
[[ "$(plutil -extract CFBundleVersion raw -expect string "$app_path/Contents/Info.plist")" == "$expected_build_number" ]] || fail "unexpected build number"
[[ "$(plutil -extract CFBundleIconName raw -expect string "$app_path/Contents/Info.plist")" == 'AppIcon' ]] || fail "app does not declare the compiled AppIcon catalog"
[[ -f "$app_path/Contents/Resources/Assets.car" ]] || fail "app does not contain the compiled asset catalog"
[[ "$(lipo -archs "$executable_path")" == 'arm64' ]] || fail "app executable must be arm64 only"
file "$executable_path" | grep -F 'arm64' >/dev/null || fail "file inspection did not report arm64"

codesign --verify --deep --strict --verbose=2 "$app_path"
codesign_details="$(codesign -dvv "$app_path" 2>&1)"
printf '%s\n' "$codesign_details" | grep -Fqx 'Signature=adhoc' || fail "app is not ad-hoc signed"
if printf '%s\n' "$codesign_details" | grep -Eq '^Authority=Developer ID|^Authority=Apple Development|^Authority=Apple Distribution'; then
    fail "app claims a publisher signing identity"
fi

# An ad-hoc preview is deliberately not notarized or trusted by Gatekeeper. A successful
# assessment would be an unexpected trust claim and must be investigated.
if spctl --assess --type execute --verbose=4 "$app_path"; then
    fail "Gatekeeper unexpectedly accepted the unsigned preview"
fi

if find "$mount_dir" -name '.DS_Store' -o -name '*.dSYM' -o -name '*.xctest' -o -name 'DerivedData' -o -name 'SourcePackages' -o -name '.git' | grep -q .; then
    fail "DMG contains prohibited build, test, cache, or source content"
fi

while IFS= read -r link; do
    target="$(readlink "$link")"
    if [[ "$link" != "$mount_dir/Applications" && "$target" == /* ]]; then
        fail "DMG contains an unexpected absolute symlink: ${link#$mount_dir/} -> $target"
    fi
done < <(find "$mount_dir" -type l -print)

printf 'DMG verification passed: %s\n' "$dmg_path"
shasum -a 256 "$dmg_path"
