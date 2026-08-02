#!/bin/bash
# Builds the approved Apple Silicon ad-hoc gabCode developer-preview DMG.
set -euo pipefail

readonly release_version='0.0.1-preview.1'
readonly marketing_version='0.0.1'
readonly build_number='1'
readonly artifact_name="gabCode-${release_version}-macos-arm64.dmg"
readonly volume_name='gabCode 0.0.1 Preview 1'
readonly script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
readonly repo_root="$(cd "$script_dir/../../.." && pwd -P)"
readonly project_path="$repo_root/src/GabCode.MacOS/gabCode.xcodeproj"
readonly scheme='gabCode'

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "required command is unavailable: $1"
}

[[ $# -eq 2 ]] || fail "usage: $0 ${release_version} <output-directory>"
[[ "$1" == "$release_version" ]] || fail "this bootstrap surface only builds ${release_version}"
[[ -n "$2" ]] || fail 'output directory must not be empty'

for command in xcodebuild xcrun codesign spctl hdiutil ditto plutil lipo file shasum; do
    require_command "$command"
done
[[ "$(uname -s)" == 'Darwin' ]] || fail 'macOS is required'
[[ "$(uname -m)" == 'arm64' ]] || fail 'an Apple Silicon Mac is required'
[[ -d "$project_path" ]] || fail "Xcode project is missing: $project_path"
[[ -f "$repo_root/LICENSE" ]] || fail 'gabCode LICENSE is missing'
[[ -f "$script_dir/THIRD-PARTY-NOTICES.txt" ]] || fail 'third-party notice is missing'

mkdir -p "$2"
readonly output_dir="$(cd "$2" && pwd -P)"
[[ "$output_dir" != '/' && "$output_dir" != "$repo_root" ]] || fail 'refusing unsafe output directory'
readonly artifact_path="$output_dir/$artifact_name"

# Preserve unrelated output. A reproducible rerun may replace only its own known artifact.
while IFS= read -r existing; do
    [[ "$existing" == "$artifact_path" ]] || fail "output directory is not clean: $existing"
done < <(find "$output_dir" -mindepth 1 -maxdepth 1 -print)
rm -f "$artifact_path"

readonly work_dir="$(mktemp -d "${TMPDIR:-/tmp}/gabcode-preview-build.XXXXXX")"
cleanup() {
    rm -rf "$work_dir"
}
trap cleanup EXIT HUP INT TERM

readonly derived_data="$work_dir/DerivedData"
readonly stage_dir="$work_dir/dmg-root"
readonly built_app="$derived_data/Build/Products/Release/gabCode.app"
readonly staged_app="$stage_dir/gabCode.app"
readonly temporary_dmg="$work_dir/$artifact_name"
mkdir -p "$stage_dir"

printf 'Resolving pinned Swift packages...\n'
xcodebuild -resolvePackageDependencies \
    -project "$project_path" \
    -scheme "$scheme" \
    -derivedDataPath "$derived_data"

printf 'Building a clean arm64 Release bundle...\n'
xcodebuild \
    -project "$project_path" \
    -scheme "$scheme" \
    -configuration Release \
    -destination 'platform=macOS,arch=arm64' \
    -derivedDataPath "$derived_data" \
    clean build \
    ARCHS=arm64 \
    ONLY_ACTIVE_ARCH=YES \
    MARKETING_VERSION="$marketing_version" \
    CURRENT_PROJECT_VERSION="$build_number" \
    CODE_SIGNING_ALLOWED=NO \
    CODE_SIGNING_REQUIRED=NO \
    CODE_SIGN_IDENTITY=

[[ -d "$built_app" ]] || fail "Release bundle was not produced: $built_app"
ditto --noqtn "$built_app" "$staged_app"

readonly info_plist="$staged_app/Contents/Info.plist"
readonly executable_path="$staged_app/Contents/MacOS/gabCode"
[[ "$(plutil -extract CFBundleShortVersionString raw -expect string "$info_plist")" == "$marketing_version" ]] || fail 'unexpected marketing version'
[[ "$(plutil -extract CFBundleVersion raw -expect string "$info_plist")" == "$build_number" ]] || fail 'unexpected build number'
[[ "$(plutil -extract CFBundleIdentifier raw -expect string "$info_plist")" == 'com.gabrewer.gabcode' ]] || fail 'unexpected bundle identifier'
[[ "$(lipo -archs "$executable_path")" == 'arm64' ]] || fail 'Release executable is not arm64-only'

# Sign nested code before enclosing bundles. The current SwiftTerm package is statically linked,
# but this handles future task-compatible nested Mach-O helpers/frameworks without using --deep
# for signing. --deep is reserved for final integrity verification.
readonly sign_list="$work_dir/sign-list.txt"
: > "$sign_list"
while IFS= read -r candidate; do
    if file "$candidate" | grep -F 'Mach-O' >/dev/null; then
        printf '%s\n' "$candidate" >> "$sign_list"
    fi
done < <(find "$staged_app/Contents" -type f -perm -111 ! -path "$executable_path" -print)
find "$staged_app/Contents" -type d \( -name '*.framework' -o -name '*.appex' -o -name '*.xpc' \) -print >> "$sign_list"

if [[ -s "$sign_list" ]]; then
    awk -F/ '{ print NF "\t" $0 }' "$sign_list" | LC_ALL=C sort -rn | cut -f2- | while IFS= read -r component; do
        codesign --force --sign - --timestamp=none "$component"
    done
fi
codesign --force --sign - --timestamp=none "$staged_app"
codesign --verify --deep --strict --verbose=2 "$staged_app"

if spctl --assess --type execute --verbose=4 "$staged_app"; then
    fail 'Gatekeeper unexpectedly accepted the ad-hoc preview'
else
    printf 'Gatekeeper rejected the ad-hoc/non-notarized preview as expected.\n'
fi

cp "$repo_root/LICENSE" "$stage_dir/LICENSE.txt"
cp "$script_dir/THIRD-PARTY-NOTICES.txt" "$stage_dir/THIRD-PARTY-NOTICES.txt"
ln -s /Applications "$stage_dir/Applications"

printf 'Creating read-only compressed DMG...\n'
hdiutil create \
    -volname "$volume_name" \
    -srcfolder "$stage_dir" \
    -format UDZO \
    -ov \
    "$temporary_dmg"
hdiutil verify "$temporary_dmg"
mv "$temporary_dmg" "$artifact_path"

printf 'Built %s\n' "$artifact_path"
shasum -a 256 "$artifact_path"
