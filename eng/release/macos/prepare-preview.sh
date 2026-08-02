#!/bin/bash
# Prepares one verified macOS preview DMG and evidence sidecar without publication.
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../../.." && pwd -P)"
fail(){ printf 'ERROR: %s\n' "$*" >&2; exit 1; }
[[ $# -eq 1 ]] || fail "usage: $0 <x.y.z-preview.n>"
version="$1"
[[ "$version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)-preview\.([1-9][0-9]*)$ ]] || fail 'version must be x.y.z-preview.n with a positive ordinal'
(( 10#${BASH_REMATCH[1]} <= 255 && 10#${BASH_REMATCH[2]} <= 255 && 10#${BASH_REMATCH[3]} <= 65535 )) || fail 'version components are out of range'
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || fail 'Apple Silicon macOS is required'
for command in git xcodebuild xcrun shasum; do command -v "$command" >/dev/null || fail "required command is unavailable: $command"; done
[[ -f "$repo_root/eng/release/preview-evidence.schema.json" ]] || fail 'preview-evidence.schema.json is missing'
git fetch origin main
git diff --quiet && git diff --cached --quiet || fail 'tracked working tree must be clean'
commit="$(git rev-parse HEAD)"; [[ "$commit" == "$(git rev-parse origin/main)" ]] || fail 'HEAD must equal reviewed origin/main'
output="$repo_root/artifacts/v$version"; artifact="gabCode-$version-macos-arm64.dmg"; evidence="gabCode-$version-macos-arm64.evidence.json"
mkdir -p "$output"
for entry in "$output"/*; do [[ ! -e "$entry" ]] && continue; case "$(basename "$entry")" in "$artifact"|"$evidence"|gabCode-$version-windows-x64.msi|gabCode-$version-windows-x64.evidence.json) ;; *) fail "unknown output must be preserved: $entry";; esac; done
if [[ -e "$output/$artifact" || -e "$output/$evidence" ]]; then [[ -f "$output/$artifact" && -f "$output/$evidence" && ! -L "$output/$artifact" && ! -L "$output/$evidence" ]] || fail 'partial, mismatched, or symlinked macOS outputs require human disposition'; "$script_dir/test-preview.sh" "$output/$artifact"; exit 0; fi
work="$(mktemp -d "${TMPDIR:-/tmp}/gabcode-macos-prepare.XXXXXX")"; trap 'rm -rf "$work"' EXIT HUP INT TERM
xcodebuild -resolvePackageDependencies -project "$repo_root/src/GabCode.MacOS/gabCode.xcodeproj" -scheme gabCode
xcodebuild -project "$repo_root/src/GabCode.MacOS/gabCode.xcodeproj" -scheme gabCode -configuration Debug -destination 'platform=macOS,arch=arm64' test
"$script_dir/build-preview.sh" "$version" "$work"
"$script_dir/test-preview.sh" "$work/$artifact"
"$script_dir/test-preview-adversarial.sh" "$work/$artifact"
bytes="$(stat -f %z "$work/$artifact")"; hash="$(shasum -a 256 "$work/$artifact" | awk '{print $1}')"
cat > "$work/$evidence" <<EOF
{"schemaVersion":1,"platform":"macos","version":"$version","sourceCommit":"$commit","evidenceFileName":"$evidence","artifact":{"fileName":"$artifact","bytes":$bytes,"sha256":"$hash"},"toolchain":{"operatingSystem":"$(sw_vers -productVersion)","architecture":"arm64","buildTool":"$(xcodebuild -version | head -1)"},"verification":{"status":"PASS","checks":["package-resolution","xctest","arm64-release-build","dmg-verification","adversarial-verification"],"completedAtUtc":"$(date -u +%Y-%m-%dT%H:%M:%SZ)"}}
EOF
mv "$work/$artifact" "$output/$artifact"; mv "$work/$evidence" "$output/$evidence"
printf 'macOS preview preparation passed.\nArtifact: %s\nEvidence: %s\nTransfer both files together; no GitHub issue, tag, or release was created.\n' "$output/$artifact" "$output/$evidence"
