# Demo — Compound Engineering on CsvHelper

Two things an agent needs:

- **Signal** — how the agent finds out it was wrong. Linters, tests, mutation testing, a rendered diagram.
- **Information** — what the agent reads before acting. `CLAUDE.md`, skills, MCP.

| # | Demo | Type | Live? |
|---|---|---|---|
| 1 | `mermaid-polish` agent | Signal | talk-through |
| 2 | Repo + `hot-path-constraints` skill | Information | talk-through |
| 3 | Opt-in analyzer rules | Signal | live |
| 4 | Allocation tests | Signal | live |
| 5 | Test gap analysis + Stryker | Signal | live |
| 6 | Property-based tests close the findings | Signal | live |
| 7 | Feedback-loop timings | Conclusion | talk-through |

---

## 1. mermaid-polish — a signal for something invisible

`.claude/agents/mermaid-polish.agent.md`

- Text-only agent writing Mermaid is flying blind: can't see overlap, truncation, parse failure.
- Renders with mermaid-cli to a scratch dir, **looks at every PNG**, iterates until clean.
- Lands **one edit per diagram**. Owns one file, one agent per file, parallel across files.

**Point:** a signal is anything that tells the agent it was wrong. Not just linters.

---

## 1b. test-quality-auditor 

Looked for quality testing "information". -> https://github.com/dotnet/skills/tree/main/plugins/dotnet-test

Extracted a cleaned up test-quality-auditor.agent.md
Used `assertion-quality`, `test-anti-patterns`, `test-smell-detection`. `test-quality-auditor`

---

## 2. The repo + hot-path-constraints

- CsvHelper streams. **Memory bounded by the widest row, not the file.** 40 GB CSV in a few MB of RAM.
- Only works if nothing in the parse loop allocates per row: reused char buffer, fields as (start, length) into it, reused record object, field cache, compiled delegates.
- 7 target frameworks (net9.0 → net462).

`.claude/skills/hot-path-constraints/SKILL.md`

- Explains 12 invariants, why each exists, review checklist.
  - Skills is loaded before touching `CsvParser`, `CsvReader`, `CsvWriter`, `FieldCache`, `ArrayHelper`, `Expressions/`, `TypeConversion/`.
- **Cites symbols, not line numbers** — doesn't decay.

---

## 3. Analyzer rules — Information becomes Signal

`.editorconfig` + `src/CsvHelper/BannedSymbols.txt`

Nothing on by default. Rules opted in one by one, scoped to `src/CsvHelper/**.cs` only. Full rule set would have been ~600 cosmetic warnings and a list everyone ignores.

### Rules at `error` — mechanical, no judgement

| Rule | Enforces | Why here |
|---|---|---|
| CA1825 | `Array.Empty<T>()` over `new T[0]` | Zero-length array allocated per member map / per compiled record |
| CA1834 | `StringBuilder.Append(char)` over `Append(string)` | Skips the string overload's length check + copy |
| CA1845 | Span-based string concat | Avoids intermediate strings |
| CA1846 | `AsSpan` over `Substring` | `Substring` allocates; fields are offsets, not strings |
| CA1860 | `Length`/`Count` over `Any()` | `Any()` allocates an enumerator |
| CA2007 | `ConfigureAwait(false)` on every await | Library code; one missing continuation can deadlock a sync waiter |

### Rules at `warning` — heuristic, known false positives

| Rule | Enforces | Why not error |
|---|---|---|
| CA1851 | Possible multiple enumeration | Guesses; 2 known hits in `CsvReader` are fine |
| CA1861 | Constant array passed as argument | Guesses; 1 known hit in `ConfigurationFunctions` is fine |

Inform without blocking a correct change.

### Banned symbols — RS0030, `error` only in `CsvParser`, `FieldCache`, `ArrayHelper`

`none` everywhere else: LINQ is fine in cold setup code.

| Banned | Message says |
|---|---|
| `System.Linq.Enumerable` | Allocates enumerator + closure per call |
| `Regex` | Allocates, slower than the char state machine |
| `string.Substring` | Allocates; pass (buffer, start, length) |
| `string.Join` | Allocates a string per call |
| `Activator.CreateInstance` | Use `ObjectCreator`, caches compiled ctor expressions |

Every message ends with `see hot-path-constraints, "<invariant name>"`:

```
error RS0030: The symbol 'Enumerable' is banned in this project: Allocates an
enumerator, and usually a closure, per call. Hot path - see hot-path-constraints,
"No LINQ, no regex, no string.Split, no Activator in the parse loop".
```

- **A signal that carries information.** Arrives exactly when needed, can't be skipped.
- Escape hatch must state a reason: `#pragma warning disable RS0030 // cold: exception path`

### What it found: 14 violations, 11 fixed, 3 = the dropped rule

- CA1825 ×6, CA1834 ×4, CA2007 ×1. None changed control flow or output.
- CA1864 ×3 in `EnumConverter` → rule dropped, code untouched.

**Point:** the rule set is designed, not switched on. Each rule, severity, and path is defensible.

**Known limits:** analyzers only run on net8/net9 legs, so `#if !NET` code is never analyzed. Banned list is a denylist: clean build is a floor, not proof.

---

## 4. Allocation tests — an executable invariant

`tests/CsvHelper.Tests/Performance/AllocationTests.cs`

- "no allocation **per row**"
- **Read N rows, read 2N, subtract.** Fixed costs cancel. What's left is one row's cost.
- `GC.GetAllocatedBytesForCurrentThread()`

| Test | Measured | Asserts |
|---|---|---|
| `GetRecords`, 3 columns | 184.0 B/row | < 300 |
| `GetRecords`, 3 of 50 columns | 184.0 B/row | < 2× the 3-column case |
| `EnumerateRecords`, 3 columns | 144.3 B/row | < `GetRecords` |

- **Second is the sharp one:** 47 unmapped columns cost zero bytes. Eager materialization would be ~17×.
- Third: record reuse saves 39.7 B/row — the record object.
- First is blunt by design: catches something large, not one extra string.

**Point:** assertion tuned to the invariant, not a round number. Can't be satisfied by fiddling.

---

## 5. Test gap analysis + mutation testing

> Tests are a signal about **code**. Mutation testing is a signal about **tests**.

### 5a. `test-gap-analysis` skill — pseudo-mutation, no tooling

- Agent **reasons** about which production changes would still pass.
- Seconds, any language, no build. Output is a guess — **unverified**.
- Siblings: `assertion-quality`, `test-anti-patterns`, `test-smell-detection`. `test-quality-auditor` agent pipelines them.

### 5b. Stryker.NET — real mutation testing

`stryker-config.json`, tool pinned in `.config/dotnet-tools.json`

- Breaks the code (`>`→`<`, `++`→`--`, deletes lines), runs the suite each time.
- Survived = that line can be wrong and nothing notices.
- Scoped to 5 hot-path files. 971 mutants, **2m31s**, **65.2%**.

| File | Score | Survived |
|---|---|---|
| `ArrayHelper` | 96.0% | 1 |
| `FieldCache` | 75.0% | 8 |
| `CsvParser` | 74.8% | 55 |
| `CsvWriter` | 59.9% | 54 |
| `CsvReader` | 53.5% | 72 |

### 5c. The pairing

- Skill **guesses** cheaply. Stryker **proves** expensively. Run both, compare.
- Skill scales to any repo with zero setup. Tool gives a number you can ratchet.

### 5d. Three findings

1. **Headline invariant unasserted.** `processFieldBuffer` doubling *is* "bounded by widest row". Every mutation survives, even inverting the guard. No test pushes a field past 1024.
2. **Refill boundary unasserted.** `bufferPosition >= charsRead` survives `<`, `>`, negation. Delimiter-split-across-reads case. `COMPOUNDENGINEERING.md` called this "not lintable" — Stryker didn't need to understand it. **The doc was wrong about itself.** (And so is this line — see 6.)
3. **Counters incidental.** `rawRow++` → `rawRow--` survives. 100% covered, zero verified.

### 5e. Importantly

- **Coverage ≠ assertions.** Crashing mutants die from any test that reaches them. Wrong-value mutants need a real assertion. Coverage can't see that gap; agent-written tests miss it most.
- **Don't chase 100%.** Deleting `Dispose(disposing: true)`, blanking an exception message — equivalent mutants. An agent told "raise the score" will assert on exception text. Judgment picks which signals deserve action.

---

## 6. Property-based tests — closing findings 1 and 2

`tests/CsvHelper.Tests/Parsing/BufferInvarianceProperties.cs` — FsCheck.Xunit, 2 properties × 200 cases, < 1 s.

### The property

- Same for both buffers: **buffer size must not change the output.** Only how often it refills or grows.
- Every case parsed twice: small buffer vs 1 MB buffer → must agree. Well-formed input → must also equal the fields the text was built from.
- Bad-data path has no oracle. Agreement between the two parses *is* the assertion.

### The generator

- Alphabet weighted towards `"`, `|`, `\r`, `\n`, `\` → most fields need escaping → go through `processFieldBuffer`.
- Lengths 0–40, **1000–1100**, 1100–5000. Clustered on the 1024 boundary and past it.
- Delimiter `||`, NewLine `\r\n` set or auto, modes RFC4180 / RFC4180 + bad data / Escape.
- Shrinker: drop a row, drop a field, halve a field.

### Finding 1 — killed

- Small leg starts `ProcessFieldBufferSize` at 8 → growth on every case.
- All 3 sites (`ProcessRFC4180Field`, `ProcessRFC4180BadField`, `ProcessEscapeField`) die in 1–3 cases:

```
Falsifiable, after 1 test (5 shrinks)
Shrunk: RFC4180 BufferSize=23 field lengths [13] text: "ca|aac| \ra\\r"""\r\n
---- System.IndexOutOfRangeException : Index was outside the bounds of the array.
```

- `>` → `>=` still survives: reallocates the same size when exactly full. Equivalent. Leave it.

### Finding 2 — equivalent mutants, not a gap

- BufferSize 1..40 splits `||` and `\r\n` across a refill constantly. Passes on the real code.
- Hand-apply the mutants (`>=` → `<`, `>`) in `ReadDelimiter` and `ReadNewLine` → **property still passes.** 400 cases, every run.
- The code agrees: the loop-top check already returns `Incomplete` on an empty buffer. The second check only decides whether the loop spins once more.
- Stryker said survived. Test-gap skill said "unasserted". The doc said "delimiter split across two reads". All wrong the same way: the boundary *is* exercised, the mutant just can't be observed.

### What the property found first: the test's own encoder

- First 3 failures were on unmutated code. Shrunk to `bc|` + `||` + `""`: parser reads `|||` greedily as delimiter then `|`. Correct. My encoder didn't quote a field ending in a delimiter prefix.
- The early "kills" of finding 2 were this bug. Fix the encoder → mutant survives → equivalent.
- Typical PBT: the oracle is as likely wrong as the code. Shrinking is what makes that cheap to tell apart.

### Point

- **Survived ≠ gap.** Stryker's output needs judgement. The property test was the cheapest judge — cheaper than reasoning about it.
- Two invariants from `hot-path-constraints` are now executable: "bounded by the widest row", "ReadLine must stay resumable".
- Stryker after, same config, 3m20s:

| | Before | After |
|---|---|---|
| Total score | 65.2% | **67.5%** |
| `CsvParser` score | 74.8% | **80.9%** |
| `CsvParser` survived | 55 | **39** |

- 16 fewer survivors from 2 properties. Only 6 were targeted; the rest were nearby (`FillBuffer` doubling, escape-state booleans).
- Still surviving, on purpose: the 6 refill mutants (equivalent) and the 3 `>=` growth mutants (equivalent).

---

## 7. Feedback-loop timings — the agent is time-blind

Measured warm on this machine, after touching `CsvParser.cs`. Signal per second is the metric.

| Loop | Time | What it tells the agent |
|---|---|---|
| `dotnet build` lib, `-f net9.0` | 2.6 s | Compiler + 8 analyzer rules + banned symbols |
| … same, `-p:RunAnalyzers=false` | 1.5 s | Analyzers cost ~1 s. Never worth skipping |
| `dotnet build` lib, all 7 TFMs | 3.6 s | API drift (the CA1864 case) |
| `dotnet test -f net9.0`, full suite, 1068 tests | 4.6 s | Incl. build |
| … `--filter ~Parsing` | 1.9 s | Iterating on the parser |
| … `--filter ~BufferInvarianceProperties`, 400 cases | 1.9 s | Both buffer invariants |
| … `--filter ~AllocationTests` | 1.4 s | Per-row allocation |
| **`dotnet test`, no `-f`** | **95 s** | Same as 4.6 s. 90 s is 3 .NET Framework legs hanging silently on Linux |
| `dotnet test -f net48` alone | 92 s | 0 tests run, **exit code 0** |
| `dotnet stryker`, 5 files | 200 s | Mutation score |
| `dotnet stryker --mutate "**/CsvParser.cs"` | 185 s | 424 mutants, half the count, 8% faster. Scoping doesn't help |

### Conclusions

- **Every loop an agent needs while implementing is under 5 s.** Build, analyzers, full suite, property tests, allocation tests. This repo is fast — if you know the flags.
- **One missing flag = 20× slower, zero extra signal.** `dotnet test` without `-f net9.0` is 95 s. The agent doesn't feel the difference. It just runs the check less often, or not at all.
- **Green with nothing behind it.** The net48 leg: 92 s, no tests, exit 0. Worse than red.
- **Always red.** The ICU culture test fails on every Linux run. `CLAUDE.md` explains it, but "Failed: 1" is what the agent reads each time. A permanently red signal teaches the agent to ignore red.
- **Stryker is a checkpoint, not a loop.** 200 s belongs before a PR, not after each edit. Scoping to one file doesn't rescue it: setup and the per-mutant suite run dominate, not mutant count.
- **The cheapest signal is the richest.** Analyzers: ~1 s, and the only loop whose error message carries the reason and a pointer to the skill.

### Steps

1. **Publish the costs in `CLAUDE.md`.** Time next to every command, so the agent can choose. Draft:

```markdown
## Feedback loops (measured warm, Linux)
- `dotnet build src/CsvHelper/CsvHelper.csproj -f net9.0` — 3 s. Compiler + analyzers. After every edit.
- `dotnet test tests/CsvHelper.Tests/CsvHelper.Tests.csproj -f net9.0` — 5 s. ALWAYS pass `-f`:
  without it 95 s, and the .NET Framework legs hang silently on Linux and run nothing.
- add `--filter "FullyQualifiedName~Parsing"` — 2 s when iterating on the parser.
- `dotnet build src/CsvHelper/CsvHelper.csproj` (all 7 TFMs) — 4 s. Before committing:
  catches APIs missing on net462 / netstandard2.0.
- `dotnet stryker` — 200 s. Before a PR, not per edit. Scoping to one file is still 185 s.
- Expected on Linux: `CultureInfoAttributeTests...InvalidAttribute` fails (ICU). "Failed: 1" is green.
```

2. **A shortened loop.** One script (or skill), two modes:
   - `check quick` ≈ 5 s: build net9.0 → Parsing tests → property tests → allocation tests. Stops at first failure.
   - `check full` ≈ 10 s: 7-TFM build → full suite on net9.0 + net8.0.
   - `check mutate`: Stryker, unscoped. 200 s, the "before PR" gate. Untested: `--since:master` mutates only changed lines and might be the only real shortcut.
3. **Make green mean green.** Skip the ICU test when not on Windows (`Skip` on a conditional fact). The default loop becomes 0 failures.
4. **Kill the 92 s nothing.** Trim `TargetFrameworks` to the .NET legs when not on Windows via `Directory.Build.props`, or at minimum put `-f` in the guidance above. Either way the framework legs stop being run on a machine that cannot run them.
5. **Same idea for the mermaid agent.** It already does this: checks `mmdc --version` before rendering, reports the missing tool instead of a silent no-op.

---

## TODO

- [ ] `COMPOUNDENGINEERING.md` §3 cites `CsvParser.cs:901` etc. while the skill cites symbols. Good self-critique: **information decays unless written to resist decay.**

## Parking lot
Will do talk-through for demo 5

- **Feedback-loop timings.** Agents are time-blind. Publish the cost next to every command.
- **Custom analyzer for sync/async drift.** `FillBuffer`/`FillBufferAsync` held together by a comment. Agent writes the linter that constrains future agents — compounding, on the nose.
