# Demo — Compound Engineering on CsvHelper


## The spine

Two things an agent needs, and they are not the same thing:

- **Signal** — Linters, tests, mutation testing, a rendered diagram.
- **Information** — `CLAUDE.md`, skills, MCP.

## Running order

| # | Demo | Type | Live? |
|---|---|---|---|
| 1 | `mermaid-polish` agent | Signal | talk-through |
| 2 | The repo + `hot-path-constraints` | Information | talk-through |
| 3 | Opt-in analyzer rules | Signal | live |
| 4 | Allocation tests | Signal | live |
| 5 | Test gap analysis + mutation testing | Signal | live |

---

## 1. mermaid-polish — a signal for something invisible

`.claude/agents/mermaid-polish.agent.md`

- A text-only agent writing Mermaid is **flying blind**. It cannot see that the diagram
  overlaps, truncates, or fails to parse.
- The agent renders with mermaid-cli into a scratch dir and **looks at every PNG**.
- Iterates until clean, then lands **one edit per diagram** — the caller sees a small
  diff and a short report, not a stream of renders.

**Point:** a signal is any way the agent finds out it was wrong. Start here because it
is not a linter — it reframes "signal" before the boring ones show up.

**Also note:** it owns one file, runs one per file, parallel across files. Scoping an
agent's blast radius is part of the design.

---

## 2. The repo — why CsvHelper is built the way it is

Set up *why* the constraints exist before showing what enforces them.

- CsvHelper streams. **Memory is bounded by the widest row, not the file size.**
- You can parse a 40 GB CSV in a few MB of RAM — but only if nothing in the parse loop
  allocates per row.
- Techniques: one reused char buffer, fields as (start, length) pointers into it,
  a reused record object, a field cache, compiled delegates instead of reflection.
- Ships for **7 target frameworks** (net9.0 → net462).

### → `hot-path-constraints` skill — Information

`.claude/skills/hot-path-constraints/SKILL.md`

- 12 invariants, why each exists, plus a review checklist.
- Loaded before touching `CsvParser`, `CsvReader`, `CsvWriter`, `FieldCache`,
  `ArrayHelper`, `Expressions/`, `TypeConversion/`.
- **Cites symbols, not line numbers** — so it does not decay as files change.
- Explicitly tells the agent to reject "just read the whole row/file first"
  simplifications. Without this, an agent will happily suggest exactly that.

**Point:** this is Stage 1 — knowledge that used to live in a maintainer's head, now
written where an agent will read it. Demos 3–5 are what happens when you mechanize it.

---

## 3. Analyzer rules — Information becomes a Signal

### 3a. Look at the rules first

`src/CsvHelper/CsvHelper.csproj` + `.editorconfig`

- **`AnalysisMode=None`** — nothing on by default; rules opted in **by path**.
  - The full rule set would have surfaced ~600 mostly-cosmetic warnings, and taught
    everyone to ignore the warning list. A baseline of zero is the whole point.
- **`AnalysisLevel` pinned to `9.0`**, not `latest` — no `global.json`, CI doesn't pin
  an SDK, so `latest` lets a new build machine change the rules under an unrelated PR.
- **6 rules at error** (`src/CsvHelper/**.cs`): CA2007, CA1825, CA1834, CA1845, CA1846,
  CA1860.
- **2 rules at warning**: CA1851, CA1861 — heuristic and false-positive-prone, so they
  inform without blocking a correct change.
- **Banned symbols** — `BannedSymbols.txt`, enforced as `RS0030`. Set to **error only
  for `CsvParser`, `FieldCache`, `ArrayHelper`**; `none` elsewhere, where LINQ is fine
  in cold setup code.

**The best bit — the error message carries the reason:**

```
error RS0030: The symbol 'Enumerable' is banned in this project: Allocates an
enumerator, and usually a closure, per call. Hot path - see hot-path-constraints,
"No LINQ, no regex, no string.Split, no Activator in the parse loop".
```

- Not "banned" — *why*, plus which skill to read.
- **A signal that carries information.** The highest-leverage docs you have, because
  they arrive exactly when needed and cannot be skipped.
- Escape hatch requires stating a reason: `#pragma warning disable RS0030 // cold: exception path`

### 3b. Then the code changes it forced

- **CA1825** (`Array.Empty<T>()` over `new T[0]`) — 6 sites. Each allocated a fresh
  zero-length array that is then only read. Zero-length arrays have no mutable state,
  so the shared singleton is observably identical, minus an allocation per member map
  (and per compiled record in `ExpressionManager`).
- 11 of 14 fixed; the rest handled by dropping one rule.

### 3c. The multi-target build as its own signal

- **CA1864 (`Dictionary.TryAdd`) was proposed and rejected** — compiles clean on net9.0,
  fails `CS1061` on four legacy legs.
- Agents love suggesting modern APIs. The 7-framework build catches it for free.

**Point:** the rule set is *designed*, not switched on. Which rules, at what severity,
scoped to which paths — every choice is defensible.

---

## 4. Allocation tests — an executable invariant

`tests/CsvHelper.Tests/Performance/AllocationTests.cs`

### The idea

- The invariant is "no allocation **per row**". A total-bytes assertion can't express
  that — startup cost swamps it.
- So: **read N rows, read 2N rows, subtract.** Every fixed cost cancels — the reader,
  its buffers, the delegate compiled once per `CsvReader`. What remains is the marginal
  cost of one row.
- `GC.GetAllocatedBytesForCurrentThread()` is **per-thread**, so it is immune to xunit's
  parallel execution.
- Compiled `NET6_0_OR_GREATER` only — the API doesn't exist on net462/47/48.

### Current measurements

| Test | Measured | Asserts |
|---|---|---|
| `GetRecords`, 3 columns | 184.0 B/row | under 300 |
| `GetRecords`, 3 of 50 columns | 184.0 B/row | under 2× the 3-column case |
| `EnumerateRecords`, 3 columns | 144.3 B/row | strictly under `GetRecords` |

- **The second is the sharp one:** 47 unmapped columns cost *exactly zero bytes*. If
  field materialization ever became eager it'd be ~17×.
- The third confirms record reuse saves 39.7 B/row — the size of the record object.
- The first is blunt **by design**: catches something large allocated per row, not one
  extra small string.

**Point:** an assertion tuned to the invariant, not to a round number. And it can't be
satisfied by fiddling — you have to actually not allocate.

---

## 5. Test gap analysis + mutation testing

The capstone. Two tools answering the same question at different cost and confidence.

> Tests are a signal about **code**.
> Mutation testing is a signal about **tests**.

### 5a. `test-gap-analysis` — pseudo-mutation, no infrastructure

`.claude/skills/test-gap-analysis/SKILL.md`

- Agent **reasons** about which production changes would still pass the suite.
- No tooling, no build, works on any language, runs in seconds.
- Output is a judgement — plausible, **unverified**.

Related skills in `.claude/skills/`, and the `test-quality-auditor` agent that pipelines
them:

- `assertion-quality` — depth, variety, false confidence
- `test-anti-patterns` — severity-ranked audit; tests that verify nothing
- `test-smell-detection` — formal testsmells.org taxonomy

### 5b. Stryker.NET — actual mutation testing

`stryker-config.json`, tool pinned in `.config/dotnet-tools.json`

- Really breaks the code — `>` → `<`, `++` → `--`, deletes lines — and **runs the suite**
  against each broken copy.
- Survived = that line can be wrong and **nothing notices**.
- Scoped to the 5 hot-path files. 971 mutants, **2m31s**, **65.2%**.

| File | Score | Survived |
|---|---|---|
| `ArrayHelper` | 96.0% | 1 |
| `FieldCache` | 75.0% | 8 |
| `CsvParser` | 74.8% | 55 |
| `CsvWriter` | 59.9% | 54 |
| `CsvReader` | 53.5% | 72 |

### 5c. The pairing — this is the actual lesson

- The skill **guesses** cheaply. Stryker **proves** expensively.
- Run the skill first, then Stryker, and **compare**: what did the agent's reasoning
  miss? What did it flag that Stryker says is fine?
- Neither replaces the other. The skill scales to any repo with zero setup; the tool
  gives you a number you can ratchet.

### 5d. The three findings

1. **The headline invariant is unasserted.** `processFieldBuffer` growth — the buffer
   whose doubling *is* "memory bounded by the widest row". Every mutation survives,
   including inverting the guard. Default size 1024, and no test drives an escaped
   field past it.
2. **The refill boundary is unasserted.** `bufferPosition >= charsRead` survives `<`,
   `>` and negation. This is the delimiter-split-across-two-reads case.
   `COMPOUNDENGINEERING.md` listed it under *"not lintable"* — true, and beside the
   point. Stryker didn't need to understand it, just broke it and asked if anything
   screamed. **The doc was wrong about itself.**
3. **Counters are incidental.** `rawRow++` → `rawRow--` survives. Fully covered by
   hundreds of tests, verified by none.

### 5e. Two things to say out loud

- **Coverage vs assertions.** Finding 3 is the cleanest teaching case: `rawRow++` has
  100% line coverage and zero verification. Mutants that *crash* get killed by any test
  that reaches them; mutants that produce *wrong values* need a real assertion. That gap
  is exactly what coverage cannot see — and what agent-written tests miss most.
- **Don't chase 100%.** Plenty of survivors are equivalent mutants: deleting
  `Dispose(disposing: true)`, blanking an exception message string. An agent told
  "raise the score" will assert on exception text. **Judgment picks which signals
  deserve action.**

### 5f. Why this is the right capstone

- It's the only signal here that **cannot be gamed by weakening tests**. Deleting a test
  makes `dotnet test` greener and the mutation score worse.
- `break: 60` turns it into a ratchet — the classic agent failure mode (weaken the test
  until it passes) now fails the build.

---

## TODO before the lesson

- [ ] **A <1 min Stryker run.** 2m31s is too slow to hold a room.
  - Demo version: scoped config over `CsvParser.cs` only — deterministic, no git dep.
  - Workflow version: `--since:master`, mutates only changed files.
- [ ] Decide live vs. talk-through for demo 5 (needs the fast run above).
- [ ] Fix line-number citations in `COMPOUNDENGINEERING.md` §3 — it cites
      `CsvParser.cs:901` etc. while `hot-path-constraints` deliberately cites symbols so
      it doesn't decay. Good self-critique to show: **information decays unless written
      to resist decay.**

## Parking lot

Ideas raised but not built — mention only if there's time.

- **Feedback-loop timings.** Agents are time-blind: they'll rerun a 3-min check 20×
  without noticing, or skip it entirely if told it's slow. Fix is cheap — publish the
  cost next to every command so the agent can pick.
- **Property-based testing.** `BufferSize`-invariance (parse at 1, 16, 4096 → identical
  results) would close finding 2; generated field lengths crossing 1024 close finding 1.
  One generic technique, both real gaps.
- **Custom analyzer for sync/async twin drift.** `FillBuffer`/`FillBufferAsync` are held
  together by a comment. The agent writing the linter rule that constrains future agents
  is the most on-the-nose demo of compounding available.
- **MCP — deliberately not here.** Nothing in this repo is remote or live. MCP earns its
  context cost for issue trackers and dashboards, not local repo facts. *Reach for a
  script before a server* is the more useful lesson.
