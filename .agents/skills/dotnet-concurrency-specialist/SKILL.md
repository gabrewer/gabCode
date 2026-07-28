---
name: dotnet-concurrency-specialist
description: Analyzes .NET concurrency, async coordination, race conditions, cancellation, and lifecycle cleanup for gabCode. Use for shared-core watchers, process management, retained terminal state, UI/background coordination, and nondeterministic tests.
metadata:
  provider: openai-codex
  model: gpt-5.6-sol
  thinking: high
---

# .NET Concurrency Specialist

Use this supporting skill to reason about a concrete concurrency or lifecycle boundary. Do not introduce synchronization merely because code is asynchronous.

## Diagnostic method

1. Identify every shared mutable state value and its owner.
2. Map UI, worker, callback, timer, watcher, process-exit, and cancellation execution contexts.
3. State the ordering guarantees that correctness requires.
4. Identify check-then-act, read-modify-write, disposal, and callback-after-shutdown races.
5. Evaluate the existing synchronization and cancellation strategy.
6. Design deterministic tests that force the dangerous ordering.
7. Prefer the smallest ownership or synchronization change that establishes the invariant.

## gabCode hotspots

Pay special attention to:

- watcher events racing with periodic Git reconciliation;
- stale command results overwriting newer normalized state;
- worktree removal while refresh or terminal work is active;
- terminal view movement racing with process exit or shutdown;
- cancellation versus graceful process termination and descendant cleanup;
- timers or callbacks firing after disposal;
- concurrent writes to the standard-input protocol stream;
- protocol response completion racing with sidecar death;
- UI thread affinity and background result publication;
- test cleanup racing with asynchronous callbacks or child processes.

## Preferred patterns

- explicit ownership of mutable state;
- async all the way without `.Result`, `.Wait()`, or sync-over-async;
- cancellation tokens for cooperative cancellation, distinct from process-kill policy;
- `TaskCompletionSource` with asynchronous continuations when manually coordinating completion;
- channels, immutable snapshots, or serialized command loops when they simplify ownership;
- `Interlocked`, locks, or semaphores only with a documented invariant;
- idempotent, bounded shutdown and disposal;
- monotonic generations/versions when stale result suppression is required.

## Reject

- `Thread.Sleep` as coordination;
- timing-dependent tests without explicit synchronization;
- fire-and-forget work without observed failure and lifecycle ownership;
- holding locks across `await`;
- broad locking that hides undefined ownership;
- disposal that assumes callbacks or process events have already stopped.

Return the state/actor map, failing interleaving, invariant, recommended pattern, and deterministic test strategy to the worker that loaded this skill.
