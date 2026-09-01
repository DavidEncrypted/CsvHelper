# Compound Engineering

Every mechanism in this repo that gives a coding agent a signal: what fires it, and
what it catches. The point is that a wrong change fails loudly and locally, instead
of being caught in review or not at all.

The invariants these protect are documented in
[`.claude/skills/hot-path-constraints/SKILL.md`](.claude/skills/hot-path-constraints/SKILL.md).

## At a glance

| Layer | Fires | Catches |
|---|---|---|
| Nullable errors | build | null-safety regressions |
| 6 CA rules at error | build | mechanical allocation and async mistakes |
| 2 CA rules at warning | build | heuristic issues, non-blocking |
| Banned symbols | build, hot path only | LINQ / Regex / Substring / Activator in the parse loop |
| 7-framework build | build | APIs that do not exist on older targets |
| 1066 xunit tests | `dotnet test` | behavior |
| 3 allocation asserts | `dotnet test` | per-row allocation regressions |
| BenchmarkDotNet | manual | throughput and bytes/op |
| CI | push, PR | all of the above except benchmarks |

## 1. Build-time

Configured in `src/CsvHelper/CsvHelper.csproj` and `.editorconfig`.

`AnalysisMode=None` — no rule is on by default. Rules are opted into by path, so the
baseline stays at zero warnings and anything that appears is real. `AnalysisLevel` is
pinned to `9.0` rather than `latest`, so a newer SDK cannot change the rule set
underneath an unrelated change.

**Errors** (`[src/CsvHelper/**.cs]`): CA2007 ConfigureAwait, CA1825 `Array.Empty`,
CA1834 `StringBuilder.Append(char)`, CA1845 span concat, CA1846 `AsSpan`, CA1860
`Length`/`Count` over `Any()`.

**Warnings**: CA1851 multiple enumeration and CA1861 constant array arguments. Both
are heuristic and false-positive, so they inform without blocking a correct change.

**Banned symbols** — `src/CsvHelper/BannedSymbols.txt`, enforced as `RS0030`. Set to
`error` only for `CsvParser`, `FieldCache` and `ArrayHelper`, the files that must not
allocate per row; `none` elsewhere, where LINQ is legitimate in cold setup and
validation code. Each entry carries a message naming the invariant it protects, so
the build output routes the reader to the reason:

```
error RS0030: The symbol 'Enumerable' is banned in this project: Allocates an
enumerator, and usually a closure, per call. Hot path - see hot-path-constraints,
"No LINQ, no regex, no string.Split, no Activator in the parse loop".
```

To suppress in a genuinely cold region of a hot-path file, state why:

```csharp
#pragma warning disable RS0030 // cold: exception path
```

**Multi-targeting** is itself a signal. The library builds for net9.0, net8.0,
netstandard2.1, netstandard2.0, net48, net47 and net462. A change using an API that
does not exist on the older legs fails there and only there. This is how the CA1864
`Dictionary.TryAdd` proposal was rejected — it compiles on net9.0 and fails with
CS1061 on four legacy legs.

## 2. Test-time

`tests/CsvHelper.Tests` — 1066 xunit tests across 245 files.

One known failure on Linux: `CultureInfoAttributeTests.CsvConfiguration_FromType_InvalidAttribute_ThrowsCultureNotFoundException`.
ICU accepts `new CultureInfo("invalid")` where Windows NLS throws. Expected, not a bug.

`InternalsVisibleTo("CsvHelper.Tests")` is set, so tests can reach internals such as
`FieldCache` directly.

**Allocation asserts** — `tests/CsvHelper.Tests/Performance/AllocationTests.cs`.
Three tests that measure `GC.GetAllocatedBytesForCurrentThread()`, which is per
thread and therefore immune to xunit's parallel execution.

Each reads N rows and 2N rows and subtracts, so every fixed cost — the reader, its
buffers, the record delegate compiled once per `CsvReader` — cancels out. What
remains is the marginal cost of one row, which is what the invariants constrain.

Current measurements:

| Test | Measured | Asserts |
|---|---|---|
| `GetRecords`, 3 columns | 184.0 B/row | under 300 |
| `GetRecords`, 3 of 50 columns | 184.0 B/row | under 2x the 3-column case |
| `EnumerateRecords`, 3 columns | 144.3 B/row | strictly under `GetRecords` |

The second is the sharp one: 47 unmapped columns cost exactly zero bytes. If field
materialization ever became eager it would be roughly 17x. The third confirms record
reuse saves 39.7 bytes per row, the size of the record object. The first is blunt by
design — it catches something large allocated per row, not one extra small string.

Compiled only for `NET6_0_OR_GREATER`; `GetAllocatedBytesForCurrentThread` does not
exist on the net462/net47/net48 legs.

## 3. Benchmarks

`performance/CsvHelper.Benchmarks`, BenchmarkDotNet 0.15.0 with `[MemoryDiagnoser]`.
Not run by CI and not gated — it reports, it does not fail.

```
dotnet run -c Release --project performance/CsvHelper.Benchmarks
```

## 4. Knowledge

- `CLAUDE.md` — project instructions loaded every session.
- `.claude/skills/hot-path-constraints/` — the twelve invariants, why each exists,
  and a review checklist. Cites symbols rather than line numbers so it does not decay
  as files change. Loaded before changing the parser, reader, writer or converters.

## 5. CI

`.github/workflows/ci.yaml`, on push and pull request, windows-latest: restore,
build Release, test. Everything in sections 1 and 2 gates here. Benchmarks do not.

## Commands

```bash
dotnet build src/CsvHelper/CsvHelper.csproj -c Release      # all 7 frameworks + analyzers
dotnet test tests/CsvHelper.Tests/CsvHelper.Tests.csproj -f net9.0 -c Release
dotnet test tests/CsvHelper.Tests/CsvHelper.Tests.csproj -f net9.0 -c Release \
  --filter "FullyQualifiedName~AllocationTests"
```

## What has no signal yet

Known gaps, so a clean build is not mistaken for full coverage.

1. **Analyzers run on 2 of 7 legs.** Shared source is covered via net9.0, but code
   inside `#if !(NETSTANDARD2_1_OR_GREATER || NET)` — `Compatibility/AsyncExtensions.cs`
   — is never analysed.
2. **Banned symbols are a denylist.** A clean build is a floor, not proof that
   nothing allocates.
3. **Sync/async twin drift.** `FillBuffer` and `FillBufferAsync` are near-identical
   by hand and carry a `// Don't forget the async method below.` comment. Nothing
   enforces it.
4. **Three invariants are not lintable**: `Field.Start` staying relative to
   `rowStartPosition`, `ReadLine` staying resumable across a buffer refill, and
   buffer references not escaping the current row.
5. **No benchmark regression gate.** Throughput can regress silently; only
   allocation is asserted.
