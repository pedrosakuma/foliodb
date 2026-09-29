# FolioDb

An embedded, single-file **document database** for .NET, following the SQLite model:
one file on disk (plus a `-wal` journal), ACID transactions, a paged B+Tree storage engine,
no server — but storing schemaless **documents** queried with a MongoDB-style language.

Written from scratch in C# for **.NET 10**, fully **Native AOT** compatible (zero trimming/AOT warnings,
no reflection — typed mapping is done by a source generator).

> The name was chosen to avoid "SQLite" (a registered trademark). FolioDb is not affiliated with SQLite.

## Features

- **Single file + WAL**, SQLite-style: write-ahead log with checksummed frames, crash recovery, automatic/manual checkpoints.
- **ACID transactions**: many concurrent readers (snapshot isolation) + one writer; auto-commit per statement.
- **Binary document format** (BSON-like: typed, length-prefixed, zero-copy field access). JSON is only used for input/output.
- **Numbers**: `int32`, `int64`, `double` and native 128-bit `decimal` (`NumberDecimal('0.1')` / `{"$numberDecimal":"0.1"}`, `$type: 'decimal'`).
  Each value keeps its type, but comparisons, indexes and sort use the exact numeric value across types
  (`5 == 5L == 5.0 == 5m`, `0.1m < 0.1`). Arithmetic (`$inc`/`$mul`) promotes int32 → int64 → double → decimal.
- **B+Tree** primary index on `_id` (auto-generated `ObjectId` when missing) and **secondary indexes** (single/compound fields, ascending/descending, dotted paths, unique, multikey on arrays).
  `CreateIndex(Document.Parse("{city:1, age:-1}"))` / `db.users.createIndex({city:1, age:-1})` stores an ordered tuple.
  The planner uses an equality prefix plus a range on the next component; sorting still uses the explicit sort stage.
  Compound indexes include documents with at least one indexed field, encoding missing components as null. Uniqueness
  applies to the complete tuple (missing and explicit null compare equal). Multiple array fields form a Cartesian
  product, limited to 1,000 keys per document; multikey queries always recheck the document. Patterns allow at most
  32 fields and do not allow `_id` as a compound component. `GetIndexes().Keys` retains the full ordered pattern;
  `DropIndex(pattern)` matches normalized directions. Generated names gain a numeric suffix on collisions.
- **Mongo-style queries**: `$eq $ne $gt $gte $lt $lte $in $nin $exists $type $size $all $elemMatch $regex $not $and $or $nor`, sort, skip, limit, projection, `explain`.
  **Covered queries**: counts and index-field/`_id` projections are answered from index keys alone when the index is exact
  (not multikey, single predicate, same-field range, or a compound prefix/range covering every predicate).
  Compound projections preserve stored numeric types and field order; nested paths, missing fields and incompatible
  stored field order fall back to reading the document. Other projections are applied on raw bytes.
  **Nested projection** uses the same path rules as filters and `$set`, with no ambiguity: on a document a segment is a
  field name; on an array a numeric segment is a position (`{'tags.0': 1}` → `tags: ['x']`) and any other segment
  applies to every element (`{'itens.nome': 1}` → `itens: [{nome: 'a'}, {nome: 'b'}]`). Arrays stay arrays. Specs that
  would be ambiguous are rejected: a path together with its prefix (`{end: 1, 'end.cep': 1}`) or positions mixed with
  field names under the same parent (`{'itens.0': 1, 'itens.nome': 1}`).
  **Projection operators**: `$slice: n | -n | [skip, limit]` trims arrays (alone it keeps every other field; non-arrays
  are returned as is); `$elemMatch: {...}` on a top-level array keeps only the first matching element (field omitted
  when nothing matches) and implies an inclusion projection.
- **Updates**: `$set $unset $inc $mul $min $max $rename $push($each) $addToSet $pull $pop $currentDate`, replace, upsert. Same-size scalar updates are patched in place (no document rewrite).
- **Aggregation**: `$match`, `$project`, `$group`, `$sort`, `$skip`, `$limit`, `$count`, `$unwind`; group accumulators
  `$sum`, `$avg`, `$min`, `$max`, `$count`, `$push`, `$first`, `$last`. A leading `$match` uses the query planner/indexes.
- **Source generator** for typed POCOs/records (`[FolioDocument]`), with compile-time diagnostics.
- **`folio` CLI shell** (like `sqlite3`): REPL, scripts, `.dump`/`.import`/`.export`, `.integrity`, `.timer`.
- Tested: 120+ unit tests including a B+Tree fuzz test against a model, WAL torn-write/corruption tests,
  a concurrent bank-transfer invariant test and **kill -9 crash tests** against the native binary.

## Quick start

```csharp
using FolioDb;

using var db = FolioDatabase.Open("app.folio");
var users = db.GetCollection("users");

users.CreateIndex("email", unique: true);
users.Insert("""{ "name": "Ana", "email": "ana@example.com", "age": 31, "tags": ["admin"] }""");

var adults = users.Find("""{ "age": { "$gte": 18 } }""",
    new FindOptions { Sort = Document.Parse("""{ "age": -1 }"""), Limit = 10 });

users.UpdateOne("""{ "email": "ana@example.com" }""", """{ "$inc": { "age": 1 }, "$push": { "tags": "owner" } }""");
Console.WriteLine(users.Explain("""{ "email": "ana@example.com" }""")); // IXSCAN email_1 ...
```

### Transactions and snapshots

```csharp
using (var tx = db.BeginTransaction())
{
    var accounts = tx.GetCollection("accounts");
    accounts.UpdateOne("""{ "_id": 1 }""", """{ "$inc": { "balance": -50 } }""");
    accounts.UpdateOne("""{ "_id": 2 }""", """{ "$inc": { "balance": 50 } }""");
    tx.Commit();                      // Dispose without Commit = rollback
}

using var snap = db.BeginSnapshot();  // consistent read-only view, never blocks the writer
long total = snap.GetCollection("accounts").Count();
```

Writers are serialized by an in-process lock; a writer waiting longer than `BusyTimeout` gets
`"database is busy"`. Readers never block and never see uncommitted data.

The current file format is version 3. Version 2 databases remain readable and are upgraded on the next header write;
after that, older binaries reject them. Keep a backup before upgrading if you need to return to an older binary.

### Aggregation

```csharp
var totals = db.GetCollection("orders").Aggregate("""
    [
      {$match: {status: 'paid'}},
      {$unwind: '$lines'},
      {$group: {_id: '$lines.sku', quantity: {$sum: '$lines.qty'}, orders: {$count: {}}}},
      {$sort: {quantity: -1}},
      {$limit: 10}
    ]
    """);
```

The same pipeline works in `db.orders.aggregate([...])` in the shell. The C# API also accepts
`IEnumerable<Document>`; typed collections return untyped documents because pipelines change the result shape.
The whole pipeline runs in one read snapshot (or the caller's transaction/snapshot), without modifying stored rows.

- `$project` uses find-projection rules, including `$slice`/`$elemMatch`; it does **not** compute expressions.
- Group expressions accept constants, `$field.path` references, object/array expressions, and `{$literal: value}`.
  Field references resolve document fields and numeric array positions; use `$unwind` before referencing fields of
  array elements. Missing references become null. Unsupported expression operators/variables are errors.
- Group keys use the same exact comparisons as indexes (numeric types with equal values share a group); the first
  encountered key retains its stored type. `$sum`/`$avg` ignore non-numbers; an empty numeric input yields 0/null.
  Arithmetic shares update promotion rules; integer/decimal overflow is an error. Decimal averages stay decimal,
  others return double. `$min`/`$max` ignore null/missing and use the database's type order.
- `$unwind: '$path'` emits one row per element; missing/null/empty arrays emit none, and non-array values emit one.
  The object/options form of `$unwind` is not supported.
- `$sort` is stable. Use it before `$first`, `$last` or `$push` when order matters; unsorted scan/group order is not
  guaranteed. `$skip`/`$limit` accept integers from 0 to `int.MaxValue` (`$limit:0` returns no rows). `$count:'name'`
  emits an int64 count, or no row for empty input.
- All stages are validated even on empty collections. Results and intermediate stages are materialized in memory;
  there is no disk spilling or streaming cursor yet.

### Typed documents (source generator, AOT-safe)

```csharp
[FolioDocument]
public partial record Order
{
    public ObjectId Id { get; init; }                 // maps to _id
    public required string Customer { get; init; }    // field "customer" (camelCase by default)
    [FolioField("total_cents")] public long Total { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
    public OrderStatus Status { get; set; }
    [FolioIgnore] public string? Cache { get; set; }
}

[FolioDocument]
public partial record OrderLine(string Sku, int Qty);

var orders = db.GetCollection<Order>("orders");
orders.Insert(new Order { Customer = "ana", Lines = [new("A1", 2)] });
Order? o = orders.FindOne("""{ "customer": "ana" }""");
```

The generator implements `IFolioDocument<T>` (`ToDocument`/`FromDocument`). Supported members: primitives,
`string`, enums, nullables, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeSpan`, `Guid`, `decimal`,
`ObjectId`, `byte[]`, `Document`/`DocValue`, lists/arrays/sets, `Dictionary<string, T>` and nested `[FolioDocument]` types.
Diagnostics: `FOLIO001` type not partial, `FOLIO002` unsupported member type, `FOLIO003` no usable constructor, `FOLIO004` duplicate field name.

### Options

```csharp
FolioDatabase.Open("app.folio", new FolioOptions
{
    PageSize = 4096,                          // new databases only (1024..32768)
    CacheSizePages = 4096,
    AutoCheckpointFrames = 1000,              // 0 = manual db.Checkpoint() only
    Synchronous = SynchronousMode.Full,       // Full | Normal | Off, as PRAGMA synchronous
    BusyTimeout = TimeSpan.FromSeconds(30),
});
```

## CLI

```
$ folio app.folio
FolioDb shell — /path/to/app.folio
Enter ".help" for usage hints.
folio> db.users.insert({ name: 'Bob', age: 40 })
folio> db.users.find({ age: { $gt: 30 } }).sort({ age: -1 }).limit(5)
folio> db.users.createIndex({ age: 1 })
folio> db.users.find({ age: 40 }).explain()
folio> begin
folio> db.users.updateMany({}, { $inc: { age: 1 } })
folio> commit
folio> .dump > backup.js
```

- Non-interactive: `folio app.folio -c "db.users.count()"`, `folio app.folio < script.js`, options `-bail`, `-json`, `-pretty`.
- Dot commands: `.help .collections .indexes .stats .checkpoint .integrity .timer .mode .dump .export .import .read .quit`.

## Architecture

| Layer | Files | Notes |
|---|---|---|
| Documents | `Documents/*` | `Document`/`DocValue` model, binary serializer, `RawDocument` zero-copy reader, relaxed JSON (`ObjectId()`, `ISODate()`, single quotes). |
| Key encoding | `KeyEncoder.cs` | Order-preserving, memcmp-comparable encoding of any value (type rank + big-endian/escaped payload), so B+Trees compare raw bytes. Numbers of every type share one exact encoding: nearest double + integer remainder (+ 128-bit fraction only for non-double-representable decimals), computed with `Int128` arithmetic. |
| Pager + WAL | `Storage/Pager.cs`, `StorageTx.cs` | Fixed-size pages, page cache, WAL frames with salts + cumulative checksums; commit = commit frame (+ fsync). Recovery replays only fully committed, checksum-valid frames. Readers pin a WAL snapshot (`mxFrame`). |
| B+Tree | `Storage/BTree.cs` | Variable-length keys/values, overflow pages for large documents, copy-on-write via the transaction page set. |
| Catalog / engine | `Engine/*` | Collections and index metadata in a catalog tree; index maintenance on insert/update/delete; unique constraints. |
| Query | `Query/*` | Filter compiler over raw documents, planner (`IDHACK` / `IXSCAN` / `COLLSCAN`), update applier. |

File layout: page 1 holds the header (`FolioDb format 1`, page size, catalog root, freelist); the WAL file is
`<db>-wal`. Both files are opened exclusively, so **one process** owns a database at a time
(any number of threads inside it).

## Native AOT

```
dotnet publish src/FolioDb.Cli -c Release -r linux-x64 -o out   # ~3.3 MB self-contained `folio` binary
dotnet publish tests/FolioDb.AotSmoke -c Release -r linux-x64 -o out-smoke && out-smoke/FolioDb.AotSmoke
```

Both publish with zero warnings (`IsAotCompatible`, trimming analyzers enabled on the library).

## Building and testing

```
dotnet build FolioDb.slnx
dotnet test tests/FolioDb.Tests
dotnet run -c Release --project bench/FolioDb.Bench -- --filter '*'
```

## Benchmarks

BenchmarkDotNet, short job, Linux x64, .NET 10 (indicative only). SQLite = Microsoft.Data.Sqlite storing JSON text
with an index on `json_extract(data,'$.city')`, WAL + `synchronous=NORMAL`; LiteDB 5.0.21. FolioDb uses `SynchronousMode.Normal`.

| Scenario | FolioDb | SQLite + JSON | LiteDB |
|---|---:|---:|---:|
| Insert 1,000 docs in one transaction (with 1 secondary index) | **25.9 ms** | 50.5 ms | 101.6 ms |
| Find by `_id` (10k docs) | **2.8 µs** | 8.2 µs | 30.1 µs |
| Indexed equality, 100 results (10k docs) | 125 µs | **75 µs** | 277 µs |

The indexed-equality case materializes 100 full `Document` objects while the SQLite benchmark only reads the JSON text.

Numeric representation (`NumericBenchmarks`; a 10k-doc collection with an index on `price`):

| Operation | `double` | `decimal` (native) | `decimal` as string |
|---|---:|---:|---:|
| Serialize document | 149 ns | 167 ns | 160 ns |
| Deserialize document | 241 ns | 252 ns | 261 ns |
| Encode index key | 30 ns | 92 ns | 46 ns |
| Indexed range count (~100 hits) | 67 µs | 80 µs | 80 µs |

The string representation is only shown for cost comparison: it sorts lexicographically (`"10" < "9"`), so it is not a valid option.

Update rewrite cost (`UpdateRewriteBenchmarks`, one `$inc` per document). When an operator update only overwrites existing
scalars with values of the same encoded size (`$inc`/`$mul`/`$min`/`$max`/`$set` without type growth, `$currentDate`), the
stored bytes are patched in place and only the B+Tree leaf or the overflow pages that actually changed are written to the WAL.
Anything else (new fields, growing strings, int32 → int64 promotion, array operators) re-serializes the whole document,
and a secondary index is only touched when its top-level field actually changed:

| Document size | Full rewrite | In-place patch |
|---|---:|---:|
| 200 B | 3.7 µs | 3.5 µs |
| 4 KB (overflow) | 27.7 µs | 20.5 µs |
| 64 KB (overflow) | 442 µs | 72 µs |

Read path (`ReadPathBenchmarks`; 10k docs, indexes on `city` and `age`; 100 hits per `city`, ~1,100 per age range):

| Operation | Before | Covered / pushdown |
|---|---:|---:|
| `count({city})` | 126 µs | **4.2 µs** |
| `count({age: {$gte, $lt}})` | 1,035 µs | **27 µs** |
| `find({city}, {city: 1, _id: 0})` | 422 µs | **26 µs** |
| `find({city}, {_id: 1})` (numeric `_id`) | 318 µs | **14 µs** |
| `find({city})` (full documents) | 160 µs | 121 µs |

Numeric keys share one encoding across `int32`/`int64`/`double`/`decimal` (and normalize `-0.0` and decimal scale), so each
secondary index entry stores a small type hint for its value and `_id`, like MongoDB's KeyString TypeBits: 1 byte for
integers, the raw 8/16-byte payload for doubles/decimals. Covered projections rebuild the exact stored types from it;
entries without hints (written by older versions) fall back to reading the document. Documents fetched through an index
reuse a B+Tree cursor path instead of descending from the root for every lookup.

Compound read path (`CompoundIndexBenchmarks`; 10k docs, 512-byte payload, 100 candidates per city,
10 matches for `{city:42, age:{$gte:30,$lt:40}}`; five measured iterations on a shared host):

| Operation | Single `{city:1}` | Compound `{city:1,age:-1}` |
|---|---:|---:|
| Count | 65.7 µs / 5.13 KB | 5.9 µs / 6.29 KB |
| Project city + age | 70.2 µs / 8.21 KB | 8.7 µs / 10.62 KB |

The compound path avoids fetching 100 candidate documents, at the cost of higher allocation in tuple planning/decoding.

### Concurrent reads and updates

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --concurrency 3 3
```

This separate closed-loop harness reports CSV, not BenchmarkDotNet results. Arguments are seconds per trial and
repetitions (both default to 3). Dedicated worker threads start together; each issues its next request after the
previous one completes. Each scenario/mode gets a discarded one-second warmup; recorded trials use fresh databases,
and Full/Normal runs are interleaved with alternating order. Setup and integrity checks are outside the measurement.

Workload: 10,000 documents, 1 KiB payload, 256 hot IDs, random point reads and one Int64 `$inc` per write transaction,
no secondary indexes. Automatic checkpoints use the default 1,000-frame threshold. The experimental `BusyTimeout`
is **1 second**, not the engine default of 30 seconds. Committed increments are checked against successful writes,
and snapshots are checked for stable values. Unexpected failures abort the run.

The CSV separates acquisition latency (`BeginTransaction`, including snapshot creation), service time (update through
commit/release), total successful-write latency, commit-call latency (including any automatic checkpoint), and read
latency. It includes p50/p95/p99, write maximum, timed-out acquisition count/maximum, per-writer progress min/max,
and WAL size sampled every 50 ms without opening a reader. Histogram percentiles are upper-bin estimates with
error at most 2% plus 0.02 microseconds. Throughput includes final in-flight requests draining after the stop signal;
failed acquisitions are **not** included in successful-write percentiles.

Example run on 2026-09-29: .NET 10 JIT, Linux/ext4 temporary storage, shared host, three 3-second trials.
Numbers below are medians of each trial's throughput/percentile, **not** pooled percentiles or production capacity.

| Mode | Writers / readers | Reads/s | Updates/s | Read p99 | Acquire p99 | Update total p99 |
|---|---:|---:|---:|---:|---:|---:|
| Full | 0 / 4 | 762,451 | - | 11.1 us | - | - |
| Full | 1 / 0 | - | 502 | - | 4.1 us | 5.40 ms |
| Full | 4 / 0 | - | 367 | - | 162.8 ms | 166.1 ms |
| Full | 1 / 4 | 625,981 | 411 | 31.0 us | 8.6 us | 7.71 ms |
| Full | 4 / 4 | 627,137 | 419 | 33.6 us | 267.1 ms | 272.5 ms |
| Normal | 0 / 4 | 732,709 | - | 12.7 us | - | - |
| Normal | 1 / 0 | - | 29,536 | - | 0.8 us | 0.075 ms |
| Normal | 4 / 0 | - | 10,239 | - | 90.4 us | 2.54 ms |
| Normal | 1 / 4 | 362,398 | 17,778 | 47.5 us | 6.5 us | 0.599 ms |
| Normal | 4 / 4 | 471,410 | 8,785 | 38.8 us | 10.38 ms | 11.24 ms |

Full had 8 acquisition timeouts with 4 writers/no readers and 6 with 4 writers/4 readers, summed over the three
trials. Normal had none. In Full with 4 writers/4 readers, median service p50 was 2.17 ms, but acquisition p99 was
267 ms: waiting for another writer is distinct from executing the update. The semaphore does not promise FIFO.
Commit-call time dominated the uncontended Full median (1.78 ms of 1.82 ms service); this includes more than fsync,
so the experiment does not isolate disk synchronization as the only cause.

The extra 4-writer/4-reader scenario retains each reader snapshot for 100 ms. In Normal, the median sampled WAL
peak increased from **43.7 MiB to 217.6 MiB** (Full: 10.0 to 11.3 MiB). Checkpoints need a moment with no active
snapshots, not merely the expiration of the oldest one. Snapshot-case read rates are not directly comparable:
they reuse a snapshot and repeatedly read one ID rather than opening a snapshot for each random lookup.

These are initial contention measurements. Earlier blocks on the same shared host varied substantially even for
read-only baselines. The workload does not model fixed arrival rates, network requests, cold storage, secondary-index
maintenance, or multi-update batches. Closed-loop percentiles do not account for requests that would have arrived
while a worker was blocked. No write coordination or durability behavior was changed for this experiment.

### Experimental multi-file routing measurements

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --sharding 3 3
```

This benchmark-only option compares 1, 2 and 4 independent database files on the same device. It does **not**
implement sharding in the engine. Nonnegative Int32 IDs route by `id % shards`; no CLR hash or randomized string
hash is used. This restricted routing is not a general numeric-key implementation. Every worker chooses from all
256 hot IDs, rather than owning one shard. Total documents (10,000), workers (4 writers, 0 or 4 readers), payload,
and configured page-cache budget (4,096 pages divided among files) remain fixed.

Each file retains its own 1,000-frame checkpoint threshold; aggregate WAL allowance and per-file overhead therefore
increase with shard count. File sizes are sampled sequentially and summed, so the WAL peak is approximate, not an
atomic snapshot. Full/Normal and shard counts are interleaved, reversing configuration order between repetitions.
Post-run checks validate routing, document count, total increments, integrity and checkpoint completion across files.
There is no cross-file transaction, globally consistent snapshot, global unique index, or non-ID query fan-out.

Initial results, same shared Linux/ext4 host and methodology as above: median of three 3-second trials, four writers.
The shard count is appended as a `shards` CSV column (also present as 1 for `--concurrency`).

| Mode | Readers | Files | Updates/s | Reads/s | Successful update p99 | Acquisition timeouts (3 trials) |
|---|---:|---:|---:|---:|---:|---:|
| Full | 0 | 1 | 533 | - | 9.0 ms | 15 |
| Full | 0 | 2 | 788 | - | 36.9 ms | 0 |
| Full | 0 | 4 | 1,039 | - | 17.7 ms | 0 |
| Normal | 0 | 1 | 13,905 | - | 0.86 ms | 0 |
| Normal | 0 | 2 | 20,147 | - | 4.79 ms | 0 |
| Normal | 0 | 4 | 30,893 | - | 3.49 ms | 0 |
| Full | 4 | 1 | 281 | 618,066 | 141.7 ms | 15 |
| Full | 4 | 2 | 675 | 859,240 | 38.4 ms | 0 |
| Full | 4 | 4 | 828 | 930,641 | 22.9 ms | 0 |
| Normal | 4 | 1 | 9,338 | 497,017 | 10.59 ms | 0 |
| Normal | 4 | 2 | 13,719 | 506,138 | 4.99 ms | 0 |
| Normal | 4 | 4 | 21,288 | 443,281 | 3.63 ms | 0 |

Do not interpret the low successful-write p99 for one Full file/no readers as fairness: 15 acquisitions timed out
at the experimental one-second limit and are excluded from that percentile. More files improved throughput but did
not universally improve successful-operation tails or read throughput. Full mixed-workload throughput ranged from
275 to 427 updates/s with one file and 794 to 849 with four, so the roughly 3x median gain is not a stable scaling
factor. The same-device flushes still share I/O resources. Checkpoint opportunities and smaller per-file trees also
change with partitioning; these numbers do not isolate writer-lock contention alone.

These results establish potential for independent writers, not justification for transparent production sharding.
Cross-shard correctness and non-ID workloads remain unmeasured.

### Explicit transaction batching

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --batching 3 3
```

This experiment keeps one database file and compares **1, 8 and 32 updates per explicit atomic transaction**.
Scenarios use 1 writer/no readers, 4 writers/no readers, and 4 writers/4 readers, with the same data, cache,
durability modes, one-second acquisition timeout, warmup and alternating configuration order as above.
Batching cannot be combined with the multi-file experiment. This uses existing transactions; the engine is unchanged.

IDs and reusable filters are prepared before timing each transaction; repeated IDs within a batch are allowed.
Every update must modify exactly one document. After workers drain, every document's counter is compared to
per-worker counts of committed increments, in addition to total increments, document count and integrity checks.
No partially completed batch counts as success. There is no modeled request arrival rate or time waiting to fill
a batch: the caller already has all operations ready.

CSV columns `writes_s` and `writer_min`/`writer_max` remain **successful transaction counts**, preserving their
meaning in previous modes. New columns `batch_size` and `updates_s` distinguish committed document updates from
transactions. All acquisition, service, commit and `write_*` percentiles describe **whole transactions**; never
divide a percentile by batch size and label it request latency. Timeouts count rejected transaction acquisitions,
not individual updates, and are excluded from successful-transaction percentiles.

Initial shared-host run on 2026-09-29, .NET 10 JIT, same-device ext4, medians of three 3-second trials:

| Mode | Writers / readers | Updates/transaction | Updates/s | Transactions/s | Successful transaction p99 | Timeouts (3 trials) |
|---|---:|---:|---:|---:|---:|---:|
| Full | 1 / 0 | 1 | 509 | 509 | 5.29 ms | 0 |
| Full | 1 / 0 | 8 | 3,311 | 414 | 9.98 ms | 0 |
| Full | 1 / 0 | 32 | 9,076 | 284 | 14.25 ms | 0 |
| Normal | 1 / 0 | 1 | 29,522 | 29,522 | 0.074 ms | 0 |
| Normal | 1 / 0 | 8 | 58,026 | 7,253 | 1.61 ms | 0 |
| Normal | 1 / 0 | 32 | 59,067 | 1,846 | 6.71 ms | 0 |
| Full | 4 / 0 | 1 | 330 | 330 | 34.1 ms | 15 |
| Full | 4 / 0 | 8 | 2,594 | 324 | 14.8 ms | 19 |
| Full | 4 / 0 | 32 | 9,214 | 288 | 72.3 ms | 14 |
| Normal | 4 / 0 | 1 | 12,278 | 12,278 | 1.55 ms | 0 |
| Normal | 4 / 0 | 8 | 37,122 | 4,640 | 6.85 ms | 1 |
| Normal | 4 / 0 | 32 | 48,009 | 1,500 | 8.35 ms | 11 |
| Full | 4 / 4 | 1 | 333 | 333 | 223.5 ms | 11 |
| Full | 4 / 4 | 8 | 2,478 | 310 | 241.9 ms | 13 |
| Full | 4 / 4 | 32 | 9,024 | 282 | 105.3 ms | 18 |
| Normal | 4 / 4 | 1 | 7,817 | 7,817 | 11.02 ms | 0 |
| Normal | 4 / 4 | 8 | 25,491 | 3,186 | 5.51 ms | 0 |
| Normal | 4 / 4 | 32 | 34,442 | 1,076 | 11.69 ms | 5 |

For 4 writers/4 readers, Full read throughput was approximately 564k/583k/547k reads/s for batches 1/8/32;
read p99 was 50.5/43.8/50.5 us. Normal was 415k/407k/406k reads/s, with read p99 54.7/50.5/56.9 us.
Median sampled WAL peaks rose from 6.2 to 72.3 MiB in Full and 74.3 to 263.3 MiB in Normal between batches 1 and 32.
This is a higher-throughput experiment, not equal completed-work volume: larger WAL peaks are not a measurement
of WAL bytes per update. The hot working set also allows multiple updates to share dirty pages within a transaction.

Batching amortizes transaction/WAL confirmation overhead but does **not** fix writer fairness. Timeout counts and
successful-operation tails must be read together: long waits ending in a timeout disappear from those percentiles.
In uncontended Normal mode, moving from 8 to 32 operations barely improved median throughput while transaction
tail latency increased. Full mixed throughput with batch 32 ranged from 6,244 to 9,368 updates/s across trials;
shared-host variability and the closed-loop limitations still apply.

Explicit batching changes the atomicity unit: all updates commit or roll back together, and results should only
be acknowledged after `Commit()` succeeds. It is suitable when the application already has a natural batch.
It is **not automatic group commit** of independently submitted transactions and does not measure that mechanism.
For unrelated requests, fair writer admission and eventual group commit remain separate research directions;
these results do not justify silently merging requests into one transaction or weakening durability.

### Experimental FIFO writer admission

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --fairness 3 3
```

This compares direct `BeginTransaction()` with a **benchmark-only external FIFO gate**, on one file, `Full`
durability and one update per transaction. Scenarios use 1, 4 or 16 writers, each with 0 or 4 readers. The engine
and its `SemaphoreSlim` are unchanged. Baseline and FIFO are interleaved with reversed order between repetitions;
the other data, warmup, correctness checks and closed-loop limitations above still apply.

FIFO order is established when each caller appends to a linked list under a monitor, not at request-generation
time. Only the head may acquire an idle gate; expired waiters are removed. Release uses `Monitor.PulseAll`, so this
prototype includes wake-up contention and scheduler overhead. All writers participate in the gate and hold the
lease through transaction disposal; readers bypass it. A lease is released even on failure. Startup verification
checks ordered admission, expiration of head/tail waiters, progress after expiration and idempotent release.

The experimental timeout is one second for the direct engine write lock or the FIFO queue. Under FIFO the engine
write lock should be uncontended; an unexpected engine-lock timeout aborts the run rather than receiving another
reported admission timeout. Acquiring the engine snapshot can still wait for internal pager synchronization.
In-flight requests drain on stop in both variants. `wait_*` includes external admission plus `BeginTransaction`;
`service_*` and `write_*` include FIFO release, but `commit_*` stops immediately after transaction disposal and
excludes external release. Additional CSV columns are `admission`, successful acquisition `wait_max`, and semicolon-
separated per-writer successful transaction counts (`writer_counts`). Failed waits remain separately reported.

Final repeated run on the shared Linux/ext4 host (.NET 10 JIT): medians of three 3-second trials; acquisition
percentiles/maxima below include **successful acquisitions only**, while timeout totals cover all three trials.

| Writers / readers | Admission | Updates/s | Acquire p50 | Acquire p95 | Acquire p99 | Acquire max (median of trial maxima) | Timeouts |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 / 0 | Direct | 251 | 3.9 us | 6.0 us | 9.0 us | 52 us | 0 |
| 1 / 0 | FIFO | 268 | 4.4 us | 7.2 us | 21.9 us | 88 us | 0 |
| 1 / 4 | Direct | 288 | 2.2 us | 3.8 us | 6.8 us | 208 us | 0 |
| 1 / 4 | FIFO | 274 | 2.8 us | 5.3 us | 12.9 us | 281 us | 0 |
| 4 / 0 | Direct | 306 | 2.2 us | 5.1 us | 22.0 ms | 857 ms | 14 |
| 4 / 0 | FIFO | 384 | 6.7 ms | 14.8 ms | 23.4 ms | 35 ms | 0 |
| 4 / 4 | Direct | 131 | 2.8 us | 0.56 ms | 626 ms | 965 ms | 5 |
| 4 / 4 | FIFO | 134 | 21.2 ms | 41.5 ms | 55.9 ms | 67 ms | 0 |
| 16 / 0 | Direct | 338 | 1.8 us | 4.6 us | 39.9 ms | 900 ms | 117 |
| 16 / 0 | FIFO | 245 | 55.9 ms | 83.0 ms | 109.6 ms | 111 ms | 0 |
| 16 / 4 | Direct | 367 | 2.2 us | 78.6 us | 691 ms | 1,003 ms | 93 |
| 16 / 4 | FIFO | 235 | 62.9 ms | 76.7 ms | 83.0 ms | 86 ms | 0 |

Distribution matters more than the direct path's tiny median wait: one writer can repeatedly win while others
time out. In one direct 16-writer/no-reader trial, per-writer completed operations ranged from **1 to 474**;
in one FIFO trial, **all 16 completed 47 each**. The final FIFO run recorded no acquisition timeouts across all
scenarios. That is an observation, not a guarantee: a slow holder or scheduler stall can still cause FIFO timeouts.

With 16 writers/4 readers, median total successful transaction p99 fell from **691 ms to 88 ms**, but throughput
fell from **367 to 235 updates/s** (about 36%). Reads continued at approximately 571k versus 518k/s, with p99
46.5 versus 50.5 us. With 4 writers/4 readers, read rates were 408k versus 421k/s and p99 63.0 versus 59.3 us.
FIFO raises the typical writer's wait because it no longer lets a recent winner bypass older callers.

Do not infer a throughput improvement from the 4-writer/no-reader row: measured ranges overlap (direct 275–406,
FIFO 328–437 updates/s), and an earlier matrix showed FIFO slower there. The host/storage varied substantially
despite interleaving; the 4-writer/mixed scenario was particularly slow in the final run. The repeatable result
was more even progress and removal of observed one-second starvation timeouts, not a universal throughput gain.
The chosen monitor/PulseAll implementation is not a lower bound on FIFO overhead, nor does this experiment
separate wake-up costs from handoff, scheduling, cache locality and storage latency. No production admission
policy, group commit, batching, acknowledgment semantics or durability setting was changed.

## Limitations

- Single process per database file (exclusive file handles); concurrency is between threads of that process.
- No full-text search, aggregation disk spilling, joins, or general expression language.
- Inside an explicit transaction, a failing multi-document statement (`InsertMany`, `UpdateMany`, index backfill) dooms the transaction
  instead of rolling back only that statement (auto-commit mode rolls back the whole statement). A duplicate-key error on a single-document operation does not doom it.
- Maximum document size is bounded by `CollectionEngine.MaxDocumentSize`; index keys must fit in a page fraction.
