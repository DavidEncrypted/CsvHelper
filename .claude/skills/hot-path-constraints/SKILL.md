---
name: hot-path-constraints
description: The allocation and buffering invariants that keep CsvHelper streaming - memory bounded by the widest row, not the file. Load before changing or reviewing CsvParser, CsvReader, CsvWriter, FieldCache, ArrayHelper, ObjectCreator, Expressions/, or TypeConversion/, before adding a configuration option, and before accepting any "just read the whole row/file first" simplification. Also load when asked why a change allocates more, why parsing got slower, or whether a new feature belongs in the parse loop.
---

# CsvHelper hot-path constraints

CsvHelper's public promise is that reading yields one record at a time and only a
small part of the file is ever resident. That promise is not a property of the API
shape — it is held up by a specific set of invariants in the parser, reader and
writer. Breaking any of them turns a streaming library into a buffering one, usually
without failing a single test.

Apply these when writing or reviewing code in the hot path: `CsvParser.cs`,
`CsvReader.cs`, `CsvWriter.cs`, `FieldCache.cs`, `ArrayHelper.cs`, `ObjectCreator.cs`,
`Expressions/`, `TypeConversion/`.

## The invariants

### 1. Peak memory is O(widest row), never O(file)

`CsvParser` owns exactly one `char[] buffer` of `BufferSize` (default `0x1000`).
`CsvParser.FillBuffer` shifts the in-progress row's leftover chars to
index 0 and refills the rest; the buffer doubles **only** when one row cannot fit
in the entire buffer.

Consequences you must preserve:

- The parser stays **forward-only and non-seekable**.
- `RawRecord` and `Record` are valid for the current row only — the chars behind
  them are overwritten by the next `Read()`. Never hand out a reference that
  outlives the row.
- Never add a code path that requires a whole row, or the whole input, to be
  resident before it can make progress.

### 2. `ReadLine` must stay resumable

`ReadLine` is a hand-written char state machine (`ParserState.Spaces / BlankLine /
Delimiter / LineEnding / NewLine`) that returns `Incomplete` and resumes mid-delimiter
or mid-newline after a refill. That resumability is *why* the buffer can stay small —
correctness never depends on lookahead.

Any new parsing rule must be expressible as resumable state. "Peek ahead N chars" or
"scan to the end of the row first" is not an acceptable simplification; it silently
converts invariant 1 into O(file).

### 3. Fields are offsets, not strings

`CsvParser.AddField` allocates nothing. It records `CsvParser`'s private `Field`
struct of `Start / Length / QuoteCount / IsBad / IsProcessed` in a reused array.

- `Field.Start` is stored **relative to `rowStartPosition`**, so the buffer shift in
  `FillBuffer` does not invalidate it. Keep it relative.
- Access struct array elements by `ref` (`ref var field = ref fields[index]`) so the
  struct is not copied.
- Pass `(char[] buffer, int start, int length)` between internals. Do not introduce a
  string parameter into anything below `GetField`.

### 4. Strings are materialized lazily, once per row

`CsvParser.GetField(int index)` is the only place a field string is created,
and it memoizes into `processedFields[index]` behind `field.IsProcessed`. `Record`
is built on demand and cached behind `isRecordProcessed`.

A map that touches 3 of 50 columns must allocate 3 strings per row, not 50. Any
change that eagerly walks all fields — validation, logging, diagnostics, a new
callback — forfeits this for every user. Make it lazy, or make it opt-in (rule 8).

### 5. A clean field costs zero copies

`ProcessRFC4180Field` returns `new ProcessedField(newStart, newLength, buffer)` — a
`readonly struct` (`CsvParser.ProcessedField`) pointing straight into the main buffer. Only
fields that genuinely need quote or escape stripping are copied into
`processFieldBuffer`, the second reusable scratch buffer (`ProcessFieldBufferSize`,
default 1024).

Trim by mutating `ref start, ref length` (`ArrayHelper.Trim`). Never allocate a
substring to trim, split, or inspect a field.

### 6. Buffers double and are never shrunk

`buffer`, `processFieldBuffer`, `fields`, `processedFields`, `FieldCache`'s entries
and the writer's buffer all grow by `*= 2` and are never released. This is a
deliberate trade: a stable high-water mark in exchange for zero per-row GC pressure
once warm.

New per-row state goes in a reused, doubling array — not a `List<T>`, `Dictionary`,
LINQ chain, or array allocated inside the loop.

### 7. Interning must not allocate to look up

`FieldCache.GetField(char[] buffer, int start, int length)` hashes the char range directly and compares with
`entry.Value.AsSpan().SequenceEqual(...)`, so a hit returns an existing string having
allocated nothing. It is a hand-rolled `Dictionary` clone specifically to get that
span-keyed lookup — do not "simplify" it to `Dictionary<string, string>`.

Its two guards are load-bearing: it is off by default (`CacheFields = false`), and
fields longer than 128 chars are never cached, so a wide-value file cannot turn the
cache into an unbounded leak.

### 8. Anything expensive is opt-in and off by default

`CacheFields = false`, `CountBytes = false`, `MaxFieldSize = 0`,
`DetectDelimiter = false`. The `CountBytes` doc comment states the rule plainly:
*"This will slow down parsing because it needs to get the byte count of every char."*
`DetectDelimiter` allocates a string over the buffer, so it is restricted to row 1.

A new feature that costs anything per char, per field, or per row must be behind a
config flag that defaults to the cheap behavior. Never make existing users pay for a
new option they did not ask for.

### 9. No LINQ, no regex, no `string.Split`, no `Activator` in the parse loop

Reflection is paid exactly once and cached:

- `RecordCreator` caches a compiled `Func<T>` per type; `GetRecords<T>` additionally
  hoists `read` out of the row loop.
- `ObjectCreator` compiles constructor calls to expressions, keyed by
  `(Type, argTypes)`.
- `TypeConverterCache`, `namedIndexCache` (header name → index) and
  `typeConverterOptionsCache` do the same for conversion and header matching.

Hot helpers carry `[MethodImpl(MethodImplOptions.AggressiveInlining)]`. Keep new
per-type work in a cache keyed by type, resolved once, hoisted out of the loop.

### 10. Guard against unbounded growth explicitly

`MaxFieldSize` throws rather than growing. The `LineBreakInQuotedFieldIsBadData`
branch exists for the same reason, and says so:

```csharp
// This avoids growing the field (and the buffer)
// until another quote is found.
```

An unterminated quote would otherwise pull the entire file into the buffer. Any new
"keep reading until X" loop needs a matching bound.

### 11. The writer mirrors the reader

One growable `char[]`; `CsvWriter.WriteToBuffer` copies into it; and
`CsvWriter.NextRecord()` flushes **every row**, so the writer also holds
at most one record. Write delegates are cached and re-fetched only when the record
type changes. Do not batch rows before flushing.

### 12. Keep the zero-allocation escape hatch working

`CsvReader.EnumerateRecords<T>(T record)` hydrates the *same* instance each
iteration, removing the last per-row allocation. It is benchmarked with
`[MemoryDiagnoser]` in `performance/CsvHelper.Benchmarks/BenchmarkEnumerateRecords.cs`.
Changes to record hydration must not reintroduce a per-row allocation on this path.

## Review checklist

Before landing a change to the hot path, confirm:

- [ ] No allocation per char, per field, or per row on the default path — including
      hidden ones: substrings, `ToArray`, `params`, boxing, closures, iterators,
      `new char[]` inside a loop.
- [ ] New internal APIs take `(buffer, start, length)`, not `string`.
- [ ] Nothing requires a full row or full file to be resident.
- [ ] New parser state is resumable across a buffer refill.
- [ ] New per-row state lives in a reused doubling array.
- [ ] Any new cost is behind a config flag defaulting to the cheap behavior.
- [ ] Any new unbounded loop has an explicit bound.
- [ ] No reference into the buffer escapes the current row.

## Measuring

Allocation claims get measured, not asserted:

```
dotnet run -c Release --project performance/CsvHelper.Benchmarks
```

`BenchmarkEnumerateRecords` runs under `[MemoryDiagnoser]`; compare
allocated-bytes-per-op before and after. A change that moves that number up needs a
reason stated in the PR.
