#!/bin/bash
# Focused contract checks for the versioned macOS preview preparation surface.
set -euo pipefail

readonly script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
readonly repo_root="$(cd "$script_dir/../../.." && pwd -P)"
readonly prepare="$script_dir/prepare-preview.sh"
readonly build="$script_dir/build-preview.sh"
readonly verifier="$script_dir/test-preview.sh"
readonly adversarial="$script_dir/test-preview-adversarial.sh"
readonly prompt="$repo_root/.pi/prompts/build-preview-dmg.md"
readonly workflow="$repo_root/Documentation/release/local-preview-workflow.md"

fail() {
    printf 'ERROR: %s\n' "$*" >&2
    exit 1
}

require_contains() {
    grep -Fq "$2" "$1" || fail "expected '$1' to contain: $2"
}

[[ -x "$prepare" ]] || fail "missing executable preparation entry point: $prepare"
[[ -x "$build" ]] || fail "missing executable generic DMG build entry point: $build"
[[ -x "$verifier" ]] || fail "missing executable generic DMG verifier: $verifier"
[[ -x "$adversarial" ]] || fail "missing executable adversarial DMG verifier: $adversarial"
[[ -f "$prompt" ]] || fail "missing macOS build prompt: $prompt"
[[ -f "$workflow" ]] || fail "missing preview workflow: $workflow"

for script in "$prepare" "$build" "$verifier" "$adversarial"; do
    bash -n "$script"
done

require_contains "$prepare" 'git fetch origin main'
require_contains "$prepare" 'origin/main'
require_contains "$prepare" 'preview-evidence.schema.json'
require_contains "$prepare" 'test-preview-adversarial.sh'
require_contains "$prepare" 'Transfer both files together'
require_contains "$build" 'MARKETING_VERSION='
require_contains "$build" 'CURRENT_PROJECT_VERSION='
require_contains "$verifier" 'CFBundleShortVersionString'
require_contains "$verifier" 'CFBundleVersion'
require_contains "$prompt" '/build-preview-dmg'
require_contains "$prompt" 'prepare-preview.sh'
require_contains "$prompt" 'macOS'
require_contains "$workflow" 'gabCode-x.y.z-preview.n-macos-arm64.evidence.json'

# All invalid forms must stop before source preparation or artifact mutation. The absent
# preparation script is intentionally a RED baseline until the builder supplies it.
for version in '' '0.0.2-preview.0' '0.0.2-preview.-1' '0.0.2' '256.0.2-preview.3' '0.0.65536-preview.3' '00.0.2-preview.3'; do
    temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/gabcode-preview-surface.XXXXXX")"
    output="$temporary_root/artifacts"
    mkdir "$output"
    if "$prepare" "$version" "$output" >/dev/null 2>&1; then
        rm -rf "$temporary_root"
        fail "invalid preview version unexpectedly succeeded: ${version:-<empty>}"
    fi
    [[ -z "$(find "$output" -mindepth 1 -print -quit)" ]] || {
        rm -rf "$temporary_root"
        fail "invalid preview version mutated output: ${version:-<empty>}"
    }
    rm -rf "$temporary_root"
done

printf 'macOS preview preparation surface checks passed.\n'
