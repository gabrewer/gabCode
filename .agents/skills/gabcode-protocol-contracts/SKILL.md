---
name: gabcode-protocol-contracts
description: Designs and reviews gabCode's versioned NativeAOT-safe JSON-over-stdio client/core contract. Use for message shapes, framing, compatibility, source-generated System.Text.Json, cancellation, errors, process lifecycle, and protocol tests.
metadata:
  provider: openai-codex
  model: gpt-5.6-sol
  thinking: high
---

# gabCode Protocol Contracts

Use this supporting skill whenever an approved task changes the contract between a native client and the shared C# NativeAOT sidecar.

## Contract principles

- Use explicit typed messages and stable discriminators; never serialize CLR type names.
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

Because gabCode initially ships its client and sidecar together, do not invent distributed rolling-deployment machinery. Still make version mismatches fail clearly so partial upgrades and packaging errors are diagnosable.

## Test expectations

Cover the approved framing and serialization with:

- known request/response and notification examples;
- source-generated round trips;
- unknown fields and message types;
- malformed/truncated records;
- unsupported versions;
- concurrent requests and out-of-order completion when allowed;
- cancellation, sidecar exit, and pending-request failure;
- protocol purity: diagnostics never appear on standard output;
- stable representative payloads or snapshots when the selected test framework supports reviewed baselines.

Do not introduce HTTP, sockets, gRPC, Protobuf, MessagePack, or a general RPC framework unless the approved design changes the architecture.

Return the contract delta, compatibility classification, source-generation registrations, and required tests to the worker that loaded this skill.
