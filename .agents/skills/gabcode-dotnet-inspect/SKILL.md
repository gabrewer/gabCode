---
name: gabcode-dotnet-inspect
description: Inspects .NET and NuGet API surfaces for gabCode without guessing signatures. Use for WPF, NativeAOT, System.Text.Json, Windows terminal integration, framework APIs, package versions, and compatibility questions.
metadata:
  provider: openai-codex
  model: gpt-5.6-sol
  thinking: high
---

# gabCode .NET API Inspection

Use this supporting skill when a gabCode task depends on the exact API surface of a .NET platform library, NuGet package, or local assembly. It supplies evidence to a worker; it does not own implementation.

## Rules

- Inspect the actual target framework and package version from the repository first.
- Prefer API metadata and package documentation over remembered signatures.
- Use compact output while exploring, then inspect only the relevant type/member in detail.
- Record the package/version or local assembly inspected in task evidence.
- Do not install a permanent global tool or change project dependencies without approval.
- Decompilation is a last resort and must respect the dependency license.

## `dotnet-inspect` workflow

When `dnx` is available, use:

```bash
dnx dotnet-inspect -y -- type --package <Package> --oneline
dnx dotnet-inspect -y -- member '<Type<T>>' --package <Package>@<Version> --oneline
dnx dotnet-inspect -y -- member <Type> --package <Package>@<Version> -m <Member>
dnx dotnet-inspect -y -- diff --package <Package>@<Old>..<New> --oneline
dnx dotnet-inspect -y -- extensions <Type> --package <Package>
dnx dotnet-inspect -y -- implements <Interface> --package <Package>
```

Use `-n N` or `-N` for bounded output rather than piping through `head` or `tail`. Quote generic types and use `<T>`, not `<>`.

For platform libraries, use the relevant `--platform` scope. For local files, inspect the built `.dll` or `.nupkg` identified by the task.

## gabCode applications

Typical questions include:

- whether a serialization API is source-generation and NativeAOT compatible;
- the exact WPF hosting or interop surface available to the Windows client;
- package APIs and changes for a pinned terminal dependency;
- cancellation and process APIs on the selected .NET target;
- public members that would cross the client/core contract;
- differences between versions before pinning or upgrading a dependency.

Return the exact command, package/version or file, relevant signatures, and any uncertainty to the worker that loaded this skill.
