#!/usr/bin/env node
import { createHash } from "node:crypto";
import { execFile as execFileCallback } from "node:child_process";
import { lstat, mkdtemp, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { promisify } from "node:util";

const execFile = promisify(execFileCallback);
const versionPattern = /^(\d+)\.(\d+)\.(\d+)-preview$/;
const platforms = Object.freeze({
  windows: { artifactSuffix: "windows-x64.msi", evidenceSuffix: "windows-x64.evidence.json" },
  macos: { artifactSuffix: "macos-arm64.dmg", evidenceSuffix: "macos-arm64.evidence.json" },
});

function fail(message) { throw new Error(message); }
function expect(condition, message) { if (!condition) fail(message); }
function exactKeys(value, keys, name) {
  expect(value && typeof value === "object" && !Array.isArray(value), `${name} must be an object`);
  const actual = Object.keys(value).sort();
  expect(actual.length === keys.length && actual.every((key, index) => key === keys[index]), `${name} has unsupported or missing fields`);
}

export function parseVersion(version) {
  const match = versionPattern.exec(version ?? "");
  expect(match, "Version must be x.y.z-preview.");
  const numbers = match.slice(1).map(Number);
  expect(numbers.every(Number.isSafeInteger), "Version components must be safe integers.");
  expect(numbers[0] <= 255 && numbers[1] <= 255 && numbers[2] <= 65535, `Windows MSI version exceeds 255.255.65535: ${version}`);
  return { version, tag: `v${version}`, numeric: numbers.join("."), preview: undefined };
}

async function defaultExecute(command, args, { cwd, allowFailure = false } = {}) {
  try {
    const result = await execFile(command, args, { cwd, encoding: "utf8", windowsHide: true, maxBuffer: 8 * 1024 * 1024 });
    return { stdout: result.stdout ?? "", stderr: result.stderr ?? "", code: 0 };
  } catch (error) {
    if (allowFailure) return { stdout: error.stdout ?? "", stderr: error.stderr ?? "", code: error.code ?? 1 };
    fail(`${command} ${args.join(" ")} failed: ${(error.stderr || error.message).trim()}`);
  }
}

async function regularFile(path, description) {
  const item = await lstat(path).catch(() => undefined);
  expect(item?.isFile() && !item.isSymbolicLink(), `${description} must be a regular non-symlink file: ${path}`);
  return item;
}

async function sha256(path) {
  return createHash("sha256").update(await readFile(path)).digest("hex");
}

function evidenceFor(platform, expected, text) {
  let evidence;
  try { evidence = JSON.parse(text); } catch { fail(`${platform} evidence is not valid JSON.`); }
  exactKeys(evidence, ["artifact", "evidenceFileName", "platform", "schemaVersion", "sourceCommit", "toolchain", "verification", "version"], `${platform} evidence`);
  exactKeys(evidence.artifact, ["bytes", "fileName", "sha256"], `${platform} evidence artifact`);
  exactKeys(evidence.toolchain, ["architecture", "buildTool", "operatingSystem"], `${platform} evidence toolchain`);
  exactKeys(evidence.verification, ["checks", "completedAtUtc", "status"], `${platform} evidence verification`);
  expect(evidence.schemaVersion === 1 && evidence.platform === platform && evidence.version === expected.version, `${platform} evidence version/platform/schema mismatch.`);
  expect(evidence.evidenceFileName === expected.evidenceName && evidence.artifact.fileName === expected.artifactName, `${platform} evidence filename mismatch.`);
  expect(/^[0-9a-f]{40}$/.test(evidence.sourceCommit), `${platform} evidence source commit is invalid.`);
  expect(Number.isInteger(evidence.artifact.bytes) && evidence.artifact.bytes > 0 && /^[0-9a-f]{64}$/.test(evidence.artifact.sha256), `${platform} evidence artifact facts are invalid.`);
  expect(evidence.verification.status === "PASS" && Array.isArray(evidence.verification.checks) && evidence.verification.checks.length > 0, `${platform} evidence is not completed PASS evidence.`);
  return evidence;
}

export async function validateInputs({ version, artifactsRoot }) {
  parseVersion(version);
  const directory = resolve(artifactsRoot, `v${version}`);
  const expected = Object.fromEntries(Object.entries(platforms).map(([platform, suffix]) => [platform, {
    artifactName: `gabCode-${version}-${suffix.artifactSuffix}`,
    evidenceName: `gabCode-${version}-${suffix.evidenceSuffix}`,
    version,
  }]));
  const allowed = new Set([...Object.values(expected).flatMap((item) => [item.artifactName, item.evidenceName]), "SHA256SUMS.txt", "release-notes.md"]);
  const entries = await readdir(directory).catch(() => fail(`Release input directory is missing: ${directory}`));
  for (const entry of entries) expect(allowed.has(entry), `Release input directory contains an unknown entry: ${entry}`);
  for (const generated of ["SHA256SUMS.txt", "release-notes.md"]) {
    if (entries.includes(generated)) await regularFile(join(directory, generated), `Generated ${generated}`);
  }

  const facts = {};
  for (const [platform, names] of Object.entries(expected)) {
    const artifactPath = join(directory, names.artifactName);
    const evidencePath = join(directory, names.evidenceName);
    const artifactExists = entries.includes(names.artifactName);
    const evidenceExists = entries.includes(names.evidenceName);
    expect(artifactExists === evidenceExists, `Prepared ${platform} input is partial; both artifact and evidence are required.`);
    expect(artifactExists, `Prepared ${platform} input is missing.`);
    const item = await regularFile(artifactPath, `${platform} artifact`);
    await regularFile(evidencePath, `${platform} evidence`);
    const evidence = evidenceFor(platform, names, await readFile(evidencePath, "utf8"));
    const hash = await sha256(artifactPath);
    expect(evidence.artifact.bytes === item.size && evidence.artifact.sha256 === hash, `${platform} artifact bytes or SHA-256 does not match evidence.`);
    facts[platform] = { ...names, path: artifactPath, bytes: item.size, sha256: hash, evidencePath, sourceCommit: evidence.sourceCommit };
  }
  expect(facts.windows.sourceCommit === facts.macos.sourceCommit, "Windows and macOS evidence source commits differ.");
  return { version, tag: `v${version}`, directory, sourceCommit: facts.windows.sourceCommit, ...facts };
}

async function run(execute, command, args, options) { return execute(command, args, options); }

export async function preflight({ version, artifactsRoot = "artifacts", repositoryRoot = process.cwd(), execute = defaultExecute }) {
  const facts = await validateInputs({ version, artifactsRoot });
  await run(execute, "git", ["fetch", "origin", "main"], { cwd: repositoryRoot });
  const status = await run(execute, "git", ["status", "--porcelain", "--untracked-files=all"], { cwd: repositoryRoot });
  expect(status.stdout.trim() === "", "Tracked working tree must be clean before preview publication.");
  const head = (await run(execute, "git", ["rev-parse", "HEAD"], { cwd: repositoryRoot })).stdout.trim().toLowerCase();
  const main = (await run(execute, "git", ["rev-parse", "origin/main"], { cwd: repositoryRoot })).stdout.trim().toLowerCase();
  expect(head === facts.sourceCommit && main === facts.sourceCommit, `HEAD and origin/main must equal prepared source commit ${facts.sourceCommit}.`);
  await run(execute, "gh", ["auth", "status"], { cwd: repositoryRoot });
  const repo = (await run(execute, "gh", ["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], { cwd: repositoryRoot })).stdout.trim();
  expect(/^[\w.-]+\/[\w.-]+$/.test(repo), "gh repository identity is invalid.");
  const release = await run(execute, "gh", ["api", `repos/${repo}/releases/tags/${facts.tag}`], { cwd: repositoryRoot, allowFailure: true });
  expect(release.code !== 0 || release.stdout.trim() === "" || release.stdout.trim() === "[]", `A GitHub release already exists for ${facts.tag}.`);
  const tag = await run(execute, "git", ["ls-remote", "--tags", "origin", `refs/tags/${facts.tag}`], { cwd: repositoryRoot });
  expect(tag.stdout.trim() === "", `Git tag already exists for ${facts.tag}.`);
  return { ...facts, repository: repo };
}

function safeSubject(subject) {
  const value = String(subject).replace(/[\r\n\u0000-\u001f]/g, " ").trim();
  expect(value.length > 0 && value.length <= 240, "Release history contains an unsupported commit subject.");
  return value.replace(/[\\`*_{}\[\]<>]/g, "\\$&").replace(/javascript:/gi, "[redacted-protocol]");
}
function linkedSubject(subject, repository) {
  return safeSubject(subject).replace(/(^|\s)#(\d+)\b/g, `$1[#\$2](https://github.com/${repository}/issues/$2)`);
}

export async function generateReleaseNotes({ facts, repositoryRoot = process.cwd(), execute = defaultExecute }) {
  const previous = await run(execute, "git", ["describe", "--tags", "--abbrev=0", `${facts.sourceCommit}^`], { cwd: repositoryRoot, allowFailure: true });
  const previousTag = previous.code === 0 && /^v\d+\.\d+\.\d+-preview(?:\.\d+)?$/.test(previous.stdout.trim()) ? previous.stdout.trim() : undefined;
  const range = previousTag ? `${previousTag}..${facts.sourceCommit}` : facts.sourceCommit;
  const history = await run(execute, "git", ["log", "--format=%H%x1f%s", range], { cwd: repositoryRoot });
  const changes = { highlights: [], fixes: [], other: [] };
  for (const row of history.stdout.split("\n")) {
    if (!row) continue;
    const [sha, subject, ...rest] = row.split("\u001f");
    if (!/^[0-9a-f]{40}$/.test(sha) || rest.length || subject === undefined) continue;
    const entry = `- ${linkedSubject(subject, facts.repository)} (${sha.slice(0, 7)})`;
    if (/^feat(?:\([^)]*\))?:/i.test(subject)) changes.highlights.push(entry);
    else if (/^fix(?:\([^)]*\))?:/i.test(subject)) changes.fixes.push(entry);
    else changes.other.push(entry);
  }
  const section = (heading, entries, empty) => `## ${heading}\n\n${entries.length ? entries.join("\n") : empty}`;
  return [
    `# gabCode ${facts.tag} developer preview`, "",
    `Target commit: \`${facts.sourceCommit}\`${previousTag ? ` · Changes since \`${previousTag}\`` : " · Initial preview history"}`, "",
    section("Highlights", changes.highlights, "No feature-classified commits were recorded in the reviewed range."), "",
    section("Bug Fixes", changes.fixes, "No fix-classified commits were recorded in the reviewed range."), "",
    section("Other Changes", changes.other, "No additional classified commits were recorded in the reviewed range."), "",
    "## Known Limitations", "", "This is an unsigned/ad-hoc developer preview. Windows SmartScreen/Installer and macOS Gatekeeper may warn or block it. Verify the published SHA-256 before use.", "",
    "## Validation Still Needed", "", "Downloaded-file trust paths, installation/copy and launch, keyboard/focus/accessibility, terminal startup, and process cleanup remain **NOT CHECKED** pending human target-platform validation. Automated preparation and publication are implementation evidence, not human acceptance.", "",
  ].join("\n");
}

async function writeMatching(path, content) {
  const existing = await readFile(path, "utf8").catch(() => undefined);
  if (existing !== undefined) expect(existing === content, `Refusing to overwrite non-matching generated file: ${basename(path)}`);
  else await writeFile(path, content, "utf8");
}

export async function prepare({ facts, repositoryRoot = process.cwd(), execute = defaultExecute }) {
  const notes = await generateReleaseNotes({ facts, repositoryRoot, execute });
  const checksums = [facts.macos, facts.windows].sort((a, b) => a.artifactName.localeCompare(b.artifactName)).map((item) => `${item.sha256}  ${item.artifactName}`).join("\n") + "\n";
  await writeMatching(join(facts.directory, "release-notes.md"), notes);
  await writeMatching(join(facts.directory, "SHA256SUMS.txt"), checksums);
  return { notes, checksums };
}

export async function publish({ facts, repositoryRoot = process.cwd(), execute = defaultExecute, confirmation }) {
  expect(confirmation === facts.version, `Publication requires exact version confirmation: ${facts.version}`);
  const current = await validateInputs({ version: facts.version, artifactsRoot: resolve(facts.directory, "..") });
  for (const platform of ["windows", "macos"]) expect(current[platform].sha256 === facts[platform].sha256 && current[platform].bytes === facts[platform].bytes && current[platform].sourceCommit === facts.sourceCommit, `Prepared ${platform} input changed after preflight.`);
  await run(execute, "git", ["fetch", "origin", "main"], { cwd: repositoryRoot });
  const head = (await run(execute, "git", ["rev-parse", "HEAD"], { cwd: repositoryRoot })).stdout.trim().toLowerCase();
  const main = (await run(execute, "git", ["rev-parse", "origin/main"], { cwd: repositoryRoot })).stdout.trim().toLowerCase();
  expect(head === facts.sourceCommit && main === facts.sourceCommit, "Source authority changed before publication.");
  const status = await run(execute, "git", ["status", "--porcelain", "--untracked-files=all"], { cwd: repositoryRoot });
  expect(status.stdout.trim() === "", "Tracked working tree must be clean immediately before preview publication.");
  const release = await run(execute, "gh", ["api", `repos/${facts.repository}/releases/tags/${facts.tag}`], { cwd: repositoryRoot, allowFailure: true });
  expect(release.code !== 0 || release.stdout.trim() === "" || release.stdout.trim() === "[]", `A GitHub release already exists for ${facts.tag}.`);
  await run(execute, "gh", ["release", "create", facts.tag, "--repo", facts.repository, "--target", facts.sourceCommit, "--prerelease", "--title", `gabCode ${facts.tag} developer preview`, "--notes-file", join(facts.directory, "release-notes.md"), facts.windows.path, facts.macos.path, join(facts.directory, "SHA256SUMS.txt")], { cwd: repositoryRoot });
  const metadataResult = await run(execute, "gh", ["release", "view", facts.tag, "--repo", facts.repository, "--json", "tagName,targetCommitish,isPrerelease,assets,url"], { cwd: repositoryRoot });
  let metadata;
  try { metadata = JSON.parse(metadataResult.stdout); } catch { fail("gh returned invalid published-release metadata JSON."); }
  expect(metadata.tagName === facts.tag && metadata.targetCommitish === facts.sourceCommit && metadata.isPrerelease === true && typeof metadata.url === "string" && /^https:\/\//.test(metadata.url), "Published release metadata does not identify the reviewed prerelease target.");
  const expectedMetadataAssets = [facts.windows, facts.macos, { artifactName: "SHA256SUMS.txt", path: join(facts.directory, "SHA256SUMS.txt"), bytes: (await stat(join(facts.directory, "SHA256SUMS.txt"))).size }];
  expect(Array.isArray(metadata.assets) && metadata.assets.length === expectedMetadataAssets.length && expectedMetadataAssets.every((asset) => {
    const published = metadata.assets.find((candidate) => candidate.name === asset.artifactName);
    return published && Number.isInteger(published.size) && published.size === asset.bytes;
  }), "Published release metadata does not contain exactly the expected assets and sizes.");
  const downloadRoot = await mkdtemp(join(tmpdir(), "gabcode-release-download-"));
  try {
    await run(execute, "gh", ["release", "download", facts.tag, "--repo", facts.repository, "--dir", downloadRoot, "--clobber"], { cwd: repositoryRoot });
    const expectedAssets = [facts.windows, facts.macos, { artifactName: "SHA256SUMS.txt", path: join(facts.directory, "SHA256SUMS.txt") }];
    const downloaded = await readdir(downloadRoot);
    expect(downloaded.length === expectedAssets.length && expectedAssets.every((asset) => downloaded.includes(asset.artifactName)), "Downloaded release assets do not exactly match the required release inventory.");
    for (const asset of expectedAssets) {
      await regularFile(join(downloadRoot, asset.artifactName), `Downloaded ${asset.artifactName}`);
      expect((await readFile(join(downloadRoot, asset.artifactName))).equals(await readFile(asset.path)), `Downloaded ${asset.artifactName} bytes differ from the published local input.`);
    }
  } finally { await rm(downloadRoot, { recursive: true, force: true }); }
  return { tag: facts.tag, url: metadata.url };
}

function usage() { return "usage: node eng/release/preview-release.mjs <preflight|prepare|publish> --version x.y.z-preview [--artifacts-root artifacts] [--confirm x.y.z-preview]"; }
async function main() {
  const [action, ...argumentsList] = process.argv.slice(2);
  const options = Object.fromEntries(argumentsList.filter((_, index) => index % 2 === 0).map((key, index) => [key, argumentsList[index * 2 + 1]]));
  if (!action || !options["--version"] || !["preflight", "prepare", "publish"].includes(action)) fail(usage());
  const facts = await preflight({ version: options["--version"], artifactsRoot: options["--artifacts-root"] ?? "artifacts" });
  if (action === "preflight") return console.log(JSON.stringify(facts, null, 2));
  if (action === "prepare") { const prepared = await prepare({ facts }); return console.log(prepared.notes); }
  await prepare({ facts });
  console.log(JSON.stringify(await publish({ facts, confirmation: options["--confirm"] }), null, 2));
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) main().catch((error) => { console.error(`ERROR: ${error.message}`); process.exitCode = 1; });
