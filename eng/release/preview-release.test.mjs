import assert from "node:assert/strict";
import { execFile as execFileCallback } from "node:child_process";
import { copyFile, mkdtemp, mkdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const helperPath = new URL("./preview-release.mjs", import.meta.url);
const execFile = promisify(execFileCallback);
const sourceCommit = "0123456789abcdef0123456789abcdef01234567";
const version = "0.0.2-preview.3";

async function fixture() {
  const root = await mkdtemp(join(tmpdir(), "gabcode-preview-release-"));
  const artifactsRoot = join(root, "artifacts");
  const directory = join(artifactsRoot, `v${version}`);
  await mkdir(directory, { recursive: true });
  const artifacts = [
    ["windows", `gabCode-${version}-windows-x64.msi`, "windows installer"],
    ["macos", `gabCode-${version}-macos-arm64.dmg`, "macOS image"],
  ];
  for (const [platform, fileName, content] of artifacts) {
    const bytes = Buffer.from(content);
    await writeFile(join(directory, fileName), bytes);
    const sha256 = (await import("node:crypto")).createHash("sha256").update(bytes).digest("hex");
    await writeFile(join(directory, fileName.replace(/(\.msi|\.dmg)$/, ".evidence.json")), JSON.stringify({
      schemaVersion: 1,
      platform,
      version,
      sourceCommit,
      evidenceFileName: fileName.replace(/(\.msi|\.dmg)$/, ".evidence.json"),
      artifact: { fileName, bytes: bytes.length, sha256 },
      toolchain: { operatingSystem: platform, architecture: platform === "windows" ? "x64" : "arm64", buildTool: "fixture" },
      verification: { status: "PASS", checks: ["fixture"], completedAtUtc: "2026-08-02T00:00:00Z" },
    }));
  }
  return { root, artifactsRoot, directory };
}

function fakeTools({ issueState = "none", history = [] } = {}) {
  const calls = [];
  const execute = async (command, args) => {
    calls.push([command, ...args]);
    if (command === "git") {
      if (args[0] === "status") return { stdout: "", stderr: "", code: 0 };
      if (args[0] === "rev-parse") return { stdout: `${sourceCommit}\n`, stderr: "", code: 0 };
      if (args[0] === "describe") return { stdout: "v0.0.1-preview.1\n", stderr: "", code: 0 };
      if (args[0] === "log") return { stdout: history.map(({ sha = sourceCommit, subject }) => `${sha}\u001f${subject}`).join("\n"), stderr: "", code: 0 };
      return { stdout: "", stderr: "", code: 0 };
    }
    if (command === "gh") {
      if (args.join(" ") === "auth status") return { stdout: "authenticated", stderr: "", code: 0 };
      if (args[0] === "repo" && args[1] === "view") return { stdout: "gabrewer/gabCode\n", stderr: "", code: 0 };
      if (args[0] === "api" && args[1].startsWith("repos/")) return { stdout: "[]", stderr: "", code: 0 };
      if (args[0] === "issue" && args[1] === "list") return { stdout: issueState === "none" ? "[]" : JSON.stringify([{ number: 99, state: issueState, title: `🧪 Preview Release: v${version}`, body: "<!-- gabcode-preview-release-control:v1 -->" }]), stderr: "", code: 0 };
      return { stdout: "", stderr: "", code: 0 };
    }
    throw new Error(`unexpected command: ${command}`);
  };
  return { calls, execute };
}

async function load() {
  return import(`${helperPath.href}?${Date.now()}-${Math.random()}`);
}

test("CLI entry point executes on Windows-style file paths", async () => {
  await assert.rejects(
    () => execFile(process.execPath, [fileURLToPath(helperPath), "invalid"], { encoding: "utf8" }),
    (error) => error.code === 1 && /usage: node eng\/release\/preview-release\.mjs/.test(error.stderr),
  );
});

test("preflight rejects partial, extra, and tampered inputs before any gh mutation", async (t) => {
  const { root, artifactsRoot, directory } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  await rm(join(directory, `gabCode-${version}-macos-arm64.evidence.json`));
  const { preflight } = await load();
  const tools = fakeTools();
  await assert.rejects(() => preflight({ version, artifactsRoot, repositoryRoot: root, execute: tools.execute }), /partial|missing/i);
  assert.equal(tools.calls.some(([command]) => command === "gh"), false);

  await writeFile(join(directory, "unexpected.txt"), "do not delete");
  await assert.rejects(() => preflight({ version, artifactsRoot, repositoryRoot: root, execute: fakeTools().execute }), /unknown|unexpected/i);
});

test("preflight recomputes evidence facts and requires clean current origin/main", async (t) => {
  const { root, artifactsRoot, directory } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  await writeFile(join(directory, `gabCode-${version}-windows-x64.msi`), "tampered");
  const { preflight } = await load();
  const tools = fakeTools();
  await assert.rejects(() => preflight({ version, artifactsRoot, repositoryRoot: root, execute: tools.execute }), /sha256|hash|bytes/i);
  assert.equal(tools.calls.some(([command]) => command === "gh"), false);
});

test("valid preflight creates one control issue, rerun resumes it, and closed or ambiguous issue state rejects", async (t) => {
  const { root, artifactsRoot } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, ensureControlIssue } = await load();
  const tools = fakeTools();
  const facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute: tools.execute });
  const created = await ensureControlIssue({ facts, repositoryRoot: root, execute: tools.execute, templatePath: new URL("./preview-release-issue.md", import.meta.url) });
  assert.equal(created.action, "created");
  assert.equal(tools.calls.filter(([command, action]) => command === "gh" && action === "issue").length, 2);

  await assert.rejects(() => ensureControlIssue({ facts, repositoryRoot: root, execute: fakeTools({ issueState: "CLOSED" }).execute, templatePath: new URL("./preview-release-issue.md", import.meta.url) }), /closed/i);
});

test("control issue search rejects an exact-title issue without the release-control marker", async (t) => {
  const { root, artifactsRoot } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, ensureControlIssue } = await load();
  const facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute: fakeTools().execute });
  const tools = fakeTools();
  const conflictingExecute = async (command, args, options) => {
    if (command === "gh" && args[0] === "issue" && args[1] === "list") {
      return { stdout: JSON.stringify([{ number: 99, state: "OPEN", title: `🧪 Preview Release: v${version}`, body: "unrelated issue" }]), stderr: "", code: 0 };
    }
    return tools.execute(command, args, options);
  };
  await assert.rejects(() => ensureControlIssue({ facts, repositoryRoot: root, execute: conflictingExecute }), /conflict|marker/i);
});

test("control issue search rejects conflicting recorded release facts", async (t) => {
  const { root, artifactsRoot } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, ensureControlIssue } = await load();
  const facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute: fakeTools().execute });
  const tools = fakeTools();
  const conflictingExecute = async (command, args, options) => {
    if (command === "gh" && args[0] === "issue" && args[1] === "list") {
      return { stdout: JSON.stringify([{ number: 99, state: "OPEN", title: `🧪 Preview Release: v${version}`, body: `<!-- gabcode-preview-release-control:v1 -->\n**Source commit:** \`deadbeefdeadbeefdeadbeefdeadbeefdeadbeef\`` }]), stderr: "", code: 0 };
    }
    return tools.execute(command, args, options);
  };
  await assert.rejects(() => ensureControlIssue({ facts, repositoryRoot: root, execute: conflictingExecute }), /conflict|source commit|match/i);
});

test("control issue search rejects ambiguous matching open issues", async (t) => {
  const { root, artifactsRoot } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, ensureControlIssue } = await load();
  const facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute: fakeTools().execute });
  const tools = fakeTools({ issueState: "OPEN" });
  const ambiguousExecute = async (command, args, options) => {
    if (command === "gh" && args[0] === "issue" && args[1] === "list") {
      return { stdout: JSON.stringify([
        { number: 99, state: "OPEN", title: `🧪 Preview Release: v${version}`, body: "<!-- gabcode-preview-release-control:v1 -->" },
        { number: 100, state: "OPEN", title: `🧪 Preview Release: v${version}`, body: "<!-- gabcode-preview-release-control:v1 -->" },
      ]), stderr: "", code: 0 };
    }
    return tools.execute(command, args, options);
  };
  await assert.rejects(() => ensureControlIssue({ facts, repositoryRoot: root, execute: ambiguousExecute }), /multiple/i);
});

test("prepare creates deterministic checksums and safe public notes without publishing", async (t) => {
  const { root, artifactsRoot, directory } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, prepare } = await load();
  const tools = fakeTools({ history: [
    { subject: "feat: add preview release command (#31)" },
    { subject: "fix: refuse tampered evidence #32" },
    { subject: "docs: [untrusted](javascript:alert(1))\nforged" },
  ] });
  const facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute: tools.execute });
  await prepare({ facts, repositoryRoot: root, execute: tools.execute, issueNumber: 99 });
  const notes = await readFile(join(directory, "release-notes.md"), "utf8");
  const sums = await readFile(join(directory, "SHA256SUMS.txt"), "utf8");
  assert.match(notes, /## Highlights/);
  assert.match(notes, /## Bug Fixes/);
  assert.match(notes, /#31/);
  assert.doesNotMatch(notes, /javascript:|\nforged/);
  assert.equal(sums.trim().split("\n").length, 2);
  assert.equal(tools.calls.some(([command, action]) => command === "gh" && action === "release"), false);
});

test("publish downloads exactly the three release assets and verifies their bytes", async (t) => {
  const { root, artifactsRoot } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, prepare, publish } = await load();
  const base = fakeTools();
  let facts;
  const execute = async (command, args, options) => {
    if (command === "gh" && args[0] === "issue" && args[1] === "list") {
      const body = `<!-- gabcode-preview-release-control:v1 -->\n**Source commit:** \`${sourceCommit}\`\n\`${facts.windows.artifactName}\` ${facts.windows.bytes} \`${facts.windows.sha256}\`\n\`${facts.macos.artifactName}\` ${facts.macos.bytes} \`${facts.macos.sha256}\``;
      return { stdout: JSON.stringify([{ number: 99, state: "OPEN", title: `🧪 Preview Release: v${version}`, body }]), stderr: "", code: 0 };
    }
    if (command === "gh" && args[0] === "release" && args[1] === "view") {
      const assets = await Promise.all([`gabCode-${version}-windows-x64.msi`, `gabCode-${version}-macos-arm64.dmg`, "SHA256SUMS.txt"].map(async (name) => ({ name, size: (await stat(join(artifactsRoot, `v${version}`, name))).size })));
      return { stdout: JSON.stringify({ tagName: `v${version}`, targetCommitish: sourceCommit, isPrerelease: true, url: "https://example.test/release", assets }), stderr: "", code: 0 };
    }
    if (command === "gh" && args[0] === "release" && args[1] === "download") {
      const directory = args[args.indexOf("--dir") + 1];
      await mkdir(directory, { recursive: true });
      for (const name of [`gabCode-${version}-windows-x64.msi`, `gabCode-${version}-macos-arm64.dmg`, "SHA256SUMS.txt"]) {
        await copyFile(join(artifactsRoot, `v${version}`, name), join(directory, name));
      }
      return { stdout: "", stderr: "", code: 0 };
    }
    return base.execute(command, args, options);
  };
  facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute });
  await prepare({ facts, repositoryRoot: root, execute });
  await assert.doesNotReject(() => publish({ facts, repositoryRoot: root, execute, confirmation: version, issueNumber: 99 }));
  assert.equal(base.calls.filter(([command, action]) => command === "gh" && action === "issue").some((call) => call[2] === "comment" && call.includes("99")), true, "publish must record verified release evidence on the control issue");
});

test("publish rejects metadata that does not name the reviewed prerelease and exact assets", async (t) => {
  const { root, artifactsRoot } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, prepare, publish } = await load();
  const base = fakeTools();
  let facts;
  let metadataRead = false;
  const execute = async (command, args, options) => {
    if (command === "gh" && args[0] === "issue" && args[1] === "list") {
      const body = `<!-- gabcode-preview-release-control:v1 -->\n**Source commit:** \`${sourceCommit}\`\n\`${facts.windows.artifactName}\` ${facts.windows.bytes} \`${facts.windows.sha256}\`\n\`${facts.macos.artifactName}\` ${facts.macos.bytes} \`${facts.macos.sha256}\``;
      return { stdout: JSON.stringify([{ number: 99, state: "OPEN", title: `🧪 Preview Release: v${version}`, body }]), stderr: "", code: 0 };
    }
    if (command === "gh" && args[0] === "release" && args[1] === "view") {
      metadataRead = true;
      return { stdout: JSON.stringify({ tagName: `v${version}`, targetCommitish: "wrong", isPrerelease: false, assets: [] }), stderr: "", code: 0 };
    }
    return base.execute(command, args, options);
  };
  facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute });
  await prepare({ facts, repositoryRoot: root, execute });
  await assert.rejects(() => publish({ facts, repositoryRoot: root, execute, confirmation: version, issueNumber: 99 }), /metadata|target|prerelease|asset/i);
  assert.equal(metadataRead, true, "publish must inspect release metadata before download-back verification");
});

test("publish rejects evidence whose recorded source commit changes after preflight before release creation", async (t) => {
  const { root, artifactsRoot, directory } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, prepare, publish } = await load();
  const tools = fakeTools();
  const facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute: tools.execute });
  await prepare({ facts, repositoryRoot: root, execute: tools.execute });
  const evidencePath = join(directory, `gabCode-${version}-macos-arm64.evidence.json`);
  const evidence = JSON.parse(await readFile(evidencePath, "utf8"));
  evidence.sourceCommit = "fedcba9876543210fedcba9876543210fedcba98";
  await writeFile(evidencePath, JSON.stringify(evidence));
  await assert.rejects(() => publish({ facts, repositoryRoot: root, execute: tools.execute, confirmation: version }), /source commit|changed/i);
  assert.equal(tools.calls.some((call) => call[0] === "gh" && call[1] === "release" && call[2] === "create"), false, "changed evidence must block release creation");
});

test("publish repeats clean-source and control-issue checks before release creation", async (t) => {
  const { root, artifactsRoot } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, prepare, publish } = await load();
  const base = fakeTools();
  const facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute: base.execute });
  await prepare({ facts, repositoryRoot: root, execute: base.execute });
  const dirtyExecute = async (command, args, options) => {
    if (command === "git" && args[0] === "status") return { stdout: " M changed-after-prepare\n", stderr: "", code: 0 };
    return base.execute(command, args, options);
  };
  await assert.rejects(() => publish({ facts, repositoryRoot: root, execute: dirtyExecute, confirmation: version, issueNumber: 99 }), /clean|working tree/i);
  assert.equal(base.calls.some((call) => call[0] === "gh" && call[1] === "release" && call[2] === "create"), false);

  const closedTools = fakeTools();
  const closedExecute = async (command, args, options) => {
    if (command === "gh" && args[0] === "issue" && args[1] === "list") {
      const body = `<!-- gabcode-preview-release-control:v1 -->\n**Source commit:** \`${sourceCommit}\`\n\`gabCode-${version}-windows-x64.msi\` 15 \`${facts.windows.sha256}\`\n\`gabCode-${version}-macos-arm64.dmg\` 11 \`${facts.macos.sha256}\``;
      return { stdout: JSON.stringify([{ number: 99, state: "CLOSED", title: `🧪 Preview Release: v${version}`, body }]), stderr: "", code: 0 };
    }
    return closedTools.execute(command, args, options);
  };
  await assert.rejects(() => publish({ facts, repositoryRoot: root, execute: closedExecute, confirmation: version, issueNumber: 99 }), /closed|issue/i);
  assert.equal(closedTools.calls.some((call) => call[0] === "gh" && call[1] === "release" && call[2] === "create"), false);
});

test("publish requires exact version confirmation and rejects changed inputs or existing release state", async (t) => {
  const { root, artifactsRoot, directory } = await fixture();
  t.after(() => rm(root, { recursive: true, force: true }));
  const { preflight, publish } = await load();
  const tools = fakeTools();
  const facts = await preflight({ version, artifactsRoot, repositoryRoot: root, execute: tools.execute });
  await assert.rejects(() => publish({ facts, repositoryRoot: root, execute: tools.execute, confirmation: "yes" }), /exact version/i);
  await writeFile(join(directory, `gabCode-${version}-macos-arm64.dmg`), "changed");
  await assert.rejects(() => publish({ facts, repositoryRoot: root, execute: tools.execute, confirmation: version }), /changed|hash|bytes/i);
  assert.equal(tools.calls.some(([command, action]) => command === "gh" && action === "release"), false);
});
