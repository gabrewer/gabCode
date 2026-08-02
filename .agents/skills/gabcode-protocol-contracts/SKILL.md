---
name: gabcode-protocol-contracts
description: Guards against unapproved gabCode internal protocols. Use only to review a future human-approved runtime-boundary proposal; no current client/core contract exists.
metadata:
  provider: openai-codex
  model: gpt-5.6-sol
  thinking: high
---

# gabCode Protocol Proposal Guard

GabCode has no shared C# NativeAOT sidecar, companion service, or internal client/core protocol. Do not use this skill to design or implement one. Load it only to confirm that a future proposal has a separate human-approved architecture PRD and explicitly authorized sprint scope.

## Gate before any contract work

Stop and report `BLOCKED: no approved internal runtime boundary exists` unless the task names the new architecture PRD and its explicit authorization. Shared vocabulary, language-neutral fixtures, and expected outcomes are not an internal protocol.

If a future approval exists, then use explicit typed messages and stable discriminators; never serialize CLR type names.
- Define the framing rule rather than assuming that arbitrary JSON writes can be parsed safely.
- Keep protocol standard output exclusively for framed protocol records.
- Send diagnostics to standard error or another explicitly approved diagnostic channel.
- Use System.Text.Json source-generation metadata for every cross-process type.
- Keep serialization trim-safe and NativeAOT compatible; avoid reflection-dependent fallback.
- Define request identity/correlation, response, notification, error, cancellation, timeout, and process-exit behavior.
- Specify unknown message type, unsupported protocol version, malformed record, duplicate response, and oversized payload behavior.
- Make ownership and ordering explicit when more than one operation may be in flight.

## Compatibility review

For each change, classify:

- source/API compatibility inside the shared contract assembly;
- wire compatibility between client and sidecar versions;
- behavior compatibility for existing callers.

Prefer additive message types and optional fields with explicit defaults. Do not rename discriminators, repurpose fields, silently change defaults, or remove accepted input without an approved compatibility decision.

Do not assume companion-process deployment, rolling upgrades, NativeAOT serialization, or packaging behavior from gabCode's current architecture. Any future approved boundary must state its own compatibility and packaging constraints.

## Test expectations after future approval

Only after the gate is satisfied, cover the approved framing and serialization with:

- known request/response and notification examples;
- source-generated round trips;
- unknown fields and message types;
- malformed/truncated records;
- unsupported versions;
- concurrent requests and out-of-order completion when allowed;
- cancellation, sidecar exit, and pending-request failure;
- protocol purity: diagnostics never appear on standard output;
- stable representative payloads or snapshots when the selected test framework supports reviewed baselines.

Do not introduce HTTP, sockets, gRPC, Protobuf, MessagePack, a general RPC framework, a sidecar, or a shared runtime unless a new approved design changes the architecture.

Return the contract delta, compatibility classification, source-generation registrations, and required tests to the worker that loaded this skill.
