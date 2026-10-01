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
- **Explicit secondary-index rebuild**: `RebuildIndex(nameOrField)` or `RebuildIndex(pattern)` bulk-loads a new tree
  in sorted key order, then replaces its catalog root and frees the old tree in one transaction.
- **Explicit compact copy**: `VacuumInto(destination)` reconstructs every primary and secondary tree into a compact,
  new database without modifying the source.
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
- **Borrowed point reads**: `TryReadById` hands a synchronous callback a read-only `ref struct` view over the stored
  bytes (scalars, UTF-8 string/binary spans, nested documents/arrays) without building a `Document`; see below.
- **Optional borrowed queries**: `Visit` uses the existing query planner and filters with a callback per match,
  early termination and no result list. `Find`/`FindOne` remain the convenient materialized APIs.
- **Reusable filters**: `PreparedFilter` prepares a filter once for repeated materialized or borrowed reads;
  no runtime code generation, shared plan cache or change to existing JSON/document overloads.
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

### Rebuilding a secondary index

```csharp
var users = db.GetCollection("users");
users.CreateIndex(Document.Parse("{ city: 1, age: -1 }"));
bool rebuilt = users.RebuildIndex(Document.Parse("{ city: 1, age: -1 }"));
// Or users.RebuildIndex("city_1_age_-1"); for a plain ascending field, "city" also works.
```

The typed `Collection<T>` has the same overloads. `false` means the collection or matching index does not
exist; `_id_` is the primary tree and is not rebuilt (`false`). Patterns use the same validation and normalized
directions as `DropIndex`. Rebuilding an empty index succeeds. The name, ordered pattern and unique setting
are retained; multikey status and encoded value/type hints are recomputed from current documents. Duplicates
in a unique index fail with `DuplicateKeyException`. A failed rebuild in an explicit transaction dooms that
transaction: dispose/roll it back, rather than committing it. A read-only snapshot rejects the operation.

An automatic call is one atomic write; inside `BeginTransaction()` it joins that transaction. It holds the
writer admission lock for the full sort/build/commit, so other writers wait or time out; existing readers and
snapshots continue to see the old catalog and tree. After commit new readers see the new tree. WAL recovery
preserves either version, never a partially published index. A borrowed read callback in the same transaction
cannot invoke maintenance; the usual write guard applies. There is no cancellation API.

The sort keeps approximately 8 MiB of encoded entries in memory plus at most one merge head per run (up to
128 runs); entries spill to delete-on-close files in the system temporary directory. When the 128-run limit
is exceeded or temporary storage runs out, the operation fails and the transaction must roll back. A write
transaction also buffers dirty database pages and WAL frames, so its memory/WAL demand scales with the new
tree and the pages freed from the old tree. Reserve temporary disk space for both the sort runs (up to
approximately 1 GiB at the run limit) and the database WAL; a full disk can fail the operation. Sorted
bulk-loading removes fragmentation in the rebuilt index and typically improves page occupancy; it does **not**
reorganize the primary tree or shrink the database file. Freed pages return to the freelist for reuse.
Measure paired rebuild versus transactional drop/create on seeded histories with
`dotnet run -c Release --project bench/FolioDb.Bench -- --index-maintenance 3000 2`. The CSV reports
occupancy, height, elapsed time, thread allocations, sort scratch, WAL and freelist; diagnostics and
integrity checks are outside timed sections. Working-set values are before/after snapshots, not peak memory.
Running repeatedly on the target storage is recommended
before deciding whether maintenance is worthwhile.

### Compacting into a new database

```csharp
using var db = FolioDatabase.Open("app.folio");
db.VacuumInto("app.compact.folio");
// Close `db`, validate/open app.compact.folio, then perform any replacement yourself.
```

`VacuumInto` is deliberately an **explicit copy**, not an in-place file swap. It preserves the source main file and
WAL on success, cancellation, I/O failure, or process crash; it never renames, truncates, checkpoints, or deletes
either source file. The destination must be absent and cannot name the source, its `-wal`, an existing file,
directory, hard-link path, or symlink (including a dangling symlink). It is published only after a complete,
checkpointed staging database has been closed, through a non-overwriting move. If publication loses a race to an
existing destination, it fails without deleting that foreign entry. Before publication, any interrupted work is only
a private `.<destination>.vacuum-<random>.tmp` staging file in the target directory, never the requested destination;
on Unix it is created user-readable/writable only. A failed cleanup can leave that clearly incomplete staging file,
which can be removed manually after inspection.

The copy holds one source read snapshot from catalog enumeration through the final output checkpoint. Writers may
continue and the output contains exactly the state at that snapshot; source checkpoints return `false` while that
snapshot is active, so its WAL can grow. Existing explicit source snapshots are allowed. The output keeps the source
page size and format compatibility, serialised document bytes, collection names (including empty collections), index
names/patterns/uniqueness, compound directions, and multikey/type hints. Primary trees are read in `_id` order and
bulk-loaded; secondary entries are externally sorted and bulk-loaded. This removes freelist holes and B+Tree
fragmentation in every copied tree, but makes no claim about physical filesystem extent contiguity.

The output commit buffers its dirty output pages, so this release does **not** promise bounded memory: working memory
and temporary disk scale with the compact output, plus up to roughly 1 GiB of external-index sort runs. Reserve space
for the source, compact output, WAL, and sort files. `CancellationToken` is checked between collections, documents,
and sorted index entries; cancellation is not observed inside a single page write. `SynchronousMode.Full` is used for
the output and it is checkpointed before publish. The non-overwriting rename makes successful process-level
publication atomic on supported local filesystems, but FolioDb does not claim directory-entry durability across a
power loss because it cannot portably fsync the containing directory. Linux behavior is exercised in tests; Windows
uses the same .NET non-overwrite move and handle rules but is not exercised by this test run.

Run a paired seeded churn measurement with:

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --vacuum 1000 1
```

One Linux shared-host trial (4 KiB pages; 1,000 documents with compound and multikey indexes, one-third deleted)
went from **606 pages / 2,482,176 bytes / 126 free pages** to **351 pages / 1,437,696 bytes / 0 free pages** in
**52.426 ms**. The matched point-read checksum was 6,460; sampled reads improved from 21.126 to 15.203 us/operation
for primary lookups and from 53.468 to 32.496 us/operation for indexed ranges. Both files had a 32-byte empty WAL
after checkpoint. Process working set was sampled at 94.8 MiB before and 95.5 MiB after, **not a peak or an
attributable allocation measurement**; the implementation's material bound is its transaction's dirty compact-output
pages plus sort buffers/runs, so measure peak memory and disk on the deployment volume before scheduling large copies.

#### Optional FIFO writer admission

```csharp
using var db = FolioDatabase.Open("app.folio", new FolioOptions
{
    WriterAdmission = WriterAdmissionMode.Fifo,
    BusyTimeout = TimeSpan.FromSeconds(30),
});
```

`WriterAdmissionMode.Default` retains the original semaphore policy (no ordering guarantee) and remains the
default. `Fifo` admits pending callers in the order they enqueue under the admission monitor. It can improve
per-writer progress under contention, at a possible throughput cost. This is per opened database, not persisted
in the file, and works with every durability mode.

Automatic writes, explicit transactions, explicit `Checkpoint()` and `Dispose()` share the selected admission
policy. An explicit transaction holds admission through commit or rollback/disposal, including any automatic
checkpoint; automatic checkpoints do not enqueue a second time. Readers do not join this queue, although internal
pager/cache locks and I/O can still cause contention. Atomicity, WAL confirmation and durability are unchanged.

FIFO uses one `BusyTimeout` budget for admission, including entering the queue monitor, rather than an external
queue followed by a second contended semaphore. Zero means a non-blocking attempt that cannot bypass queued callers;
`Timeout.InfiniteTimeSpan` waits indefinitely. Other values must be nonnegative and at most `int.MaxValue`
milliseconds; invalid values fail at open. This is **not** a deadline for transaction execution, snapshot setup,
disk I/O or commit. FIFO cannot guarantee maximum latency or prevent timeouts caused by a slow holder or scheduling.
Timed-out/interrupted waiters are unlinked so they cannot obstruct the next caller.

Writes throw the existing busy exception on admission timeout; explicit checkpoints return false. Disposal marks
the handle closing before waiting; queued writes/checkpoints recheck that state after admission and fail with
`ObjectDisposedException` instead of starting new work. If disposal cannot acquire admission, or its wait is
interrupted, the handle becomes usable again. Active transactions still complete through the existing transaction
handle. Do not use a concurrent call to `Dispose()` as a synchronization barrier.

The current file format is version 3. Version 2 databases remain readable and are upgraded on the next header write;
after that, older binaries reject them. Keep a backup before upgrading if you need to return to an older binary.

### Borrowed point reads

```csharp
var people = db.GetCollection("people");
people.Insert("""{ "_id": 1, "name": "Ana", "age": 31, "address": { "city": "Lisboa" } }""");

// true + callback result when the id exists; false (callback not invoked, result = default) when the id or
// collection does not exist. The returned document, field names and string values are not materialized.
bool found = people.TryReadById(1, static d =>
    d.TryGetValue("name", out var name) && name.StringEquals("Ana") && d.TryGetValue("age", out var age) ? age.AsInt32 : -1,
    out int anaAge);

// Pass state explicitly (it may itself be a span) so the lambda stays static and allocation-free.
people.TryReadById(1, "Lisboa".AsSpan(), static (d, city) =>
    d.TryGetValue("address", out var a) && a.AsDocument.TryGetValue("city", out var c) && c.StringEquals(city),
    out bool inLisboa);
```

`FindById` is unchanged and still returns an independent `Document`. `TryReadById` exists on `Collection` and
`Collection<T>` in all three scopes (database auto-read, `Transaction`, `Snapshot`) and sees exactly what `FindById`
would see there, including uncommitted writes of the transaction.

- **Views**: `DocumentView` (`TryGetValue` by `string` or UTF-8 name, top-level only, first occurrence; `ContainsField`,
  `FieldCount`, field enumeration via `DocFieldView`), `DocValueView` and `DocArrayView`. Missing fields make
  `TryGetValue` return false. Scalar accessors (`AsInt32`/`AsInt64`/`AsDouble`/`AsDecimal`/`AsBoolean`/`AsDateTime`/
  `AsUnixMilliseconds`/`AsObjectId`) follow `DocValue`'s conversion rules and throw `InvalidCastException` on a type
  mismatch. `AsUtf8String`, `AsBinary` and `Utf8Name` return borrowed spans; `StringEquals` (UTF-16 or UTF-8,
  ordinal) returns false for non-strings and does not allocate. `AsDocument`/`AsArray` return nested views.
- **Explicit materialization**: `ToDocument()`, `ToDocValue()`, `ToArray()`, `GetString()` and `GetName()` allocate
  owned copies (binary buffers included) that stay valid after the callback and never alias database pages.
- **Lifetime**: the views are `ref struct`s and the callback result type cannot be a `ref struct`, so the compiler
  rejects returning a view or span, capturing it in a lambda or field, boxing it, or using it in an `async` lambda.
  Unsafe code (pointers, `Unsafe`/`MemoryMarshal` tricks) can bypass this and is unsupported.
- **Scope guard**: while a callback runs, its `Transaction` rejects writes through any of its collections,
  `DropCollection`, `Commit`, `Rollback` and `Dispose` with `InvalidOperationException`; its `Snapshot` rejects
  `Dispose`. The rejected call neither dooms nor completes the scope, and reads (including nested `TryReadById`)
  stay allowed. This is what makes borrowing inside a writable transaction safe: its private pages are modified in
  place by later writes, so the view would otherwise change under the reader. It is a per-object counter for
  same-thread reentrancy; `Transaction` and `Snapshot` are not thread-safe, so calls from another thread during the
  callback are unsupported rather than reliably detected.
- **Other scopes**: auto-read and snapshot views point at committed page images, which are never modified. Other
  writers publish new page versions, `Checkpoint()` returns false while the read is active, and even disposing the
  `FolioDatabase` inside the callback only closes the handle; the view stays readable until the callback returns.
  Inside an explicit transaction's callback, auto-commit writes, `Checkpoint()` and `FolioDatabase.Dispose()` on the
  same database wait for the write lock held by that transaction (busy error / `false` after `BusyTimeout`), as before.
- **Failures**: exceptions thrown by the callback propagate unchanged, release the implicit read transaction and do
  **not** doom an explicit transaction (a read cannot have mutated it). Lookup failures (e.g. corruption) doom it
  exactly like `FindById`. A completed transaction or disposed snapshot throws `InvalidOperationException`.
- **Copies**: inline and single-overflow-page documents are read in place. A document spanning several overflow pages
  (larger than page size − 4 bytes) is first assembled into one temporary buffer, exactly as for `FindById`; its
  fields are still not materialized. Point lookups consume the id encoding directly from a per-thread buffer
  before invoking the callback. An auto-read call still allocates its read transaction and catalog metadata;
  a capturing (non-`static`) lambda allocates a closure. Buffer growth, page cache misses and caller-side
  construction of ids (for example boxing a fresh ObjectId or decimal into DocValue) may also allocate.

### Choosing the query API

Both APIs remain available; borrowed reads are opt-in, not a replacement for `Find`.

```csharp
// Convenient: independent documents you can keep, modify, sort or pass across await.
var adults = people.Find("{age:{$gte:18}}");

// Lower allocation: only read fields needed for the calculation.
long ageSum = 0;
long visited = people.Visit("{age:{$gte:18}}", doc =>
{
    if (!doc.TryGetValue("age", out var age)) throw new InvalidOperationException("Missing age.");
    ageSum += age.AsInt64;
    return true; // false stops immediately; that callback still counts in `visited`
});
```

`Visit(Document? filter, Func<DocumentView, bool> visitor)` and its JSON-filter overload exist on `Collection`
and `Collection<T>`. `"{}"` visits everything; a missing collection or no matches returns zero. Each call uses
one snapshot for its entire scan, not a new snapshot per callback, and reuses existing primary, secondary,
compound and multikey query plans. Only matching documents reach the callback: the full filter is evaluated on
each fetched document unless the plan is covered (see Explain), in which case the index scan alone already decides it.

There is no sorting, projection, skip/limit option or async callback in this first version. Order is planner-selected,
not a stable API guarantee. Read selected fields directly, stop by returning false, and call `ToDocument()` only
for results you need to retain. The full document is fetched even when an index covers the filter; a covered `Find`
projection can therefore be preferable when it can avoid reading documents altogether.

The borrowed-view lifetime and transaction/snapshot guards described above also apply to `Visit`; nested reads
are allowed, mutations of the same transaction and disposal of the same scope are rejected. Callback exceptions
propagate without dooming the transaction; query execution failures doom it like `Find`. Do not dispose the database
during a scan: an already-borrowed view remains readable, but advancing the scan may need more I/O and fail.
Slow callbacks keep the snapshot open and can delay checkpoints and grow the WAL. Unlike a materialized list,
callbacks run progressively: effects from earlier callbacks are not undone if a later read or callback fails.
Capturing a running sum, as above, allocates a closure per call site evaluation, not per result.
Parsing/planning, page cache misses, filters such as regex and multi-page overflow may still allocate.

### Reusing filters

```csharp
var adultFilter = PreparedFilter.Parse("{age:{$gte:18}}");
// Or: PreparedFilter.FromDocument(new Document { ["age"] = new Document { ["$gte"] = 18 } });
var adults = people.Find(adultFilter);       // still returns independent documents
long count = people.Count(adultFilter);
long visited = people.Visit(adultFilter, static doc => true);
string plan = people.Explain(adultFilter);
```

Reuse the same instance across calls. `Find`, `FindOne`, `Count`, `Visit` and `Explain` accept prepared filters;
the typed collection also exposes the four reading methods (use `Untyped.Explain` for planning).
`FromDocument` deep-copies input constants, including binary buffers and nested arrays/documents, so later
mutations of the source do not affect the filter. `Parse` owns its parsed values. Prepared filters are immutable
and can be shared across threads and databases; transactions/snapshots still have their usual threading rules.

Only parsing/preparation of predicates, paths, constants and regexes is reused. A plan is selected for the
current catalog on **each** execution, so index creation/removal, multikey changes and snapshot isolation remain
visible normally. No database pages or snapshots are retained by the filter. Preparation interprets the existing
filter language and does not emit code, preserving Native AOT support.
Invalid filters fail during preparation; execution errors retain the normal read behavior.
Null/empty input to the factory means all documents; a null `PreparedFilter` passed to a collection is an error.

Preparation and the defensive copy have a one-time cost: prepare outside a hot loop. For one-off queries,
the existing JSON/document overloads remain the simplest choice. This first version has no parameter binding,
prepared updates/deletes or prepared aggregation pipelines; changing constants requires a new prepared filter.
Find options (sort/projection) are still processed per call.

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

It also emits `FromView(DocumentView)`, which builds the entity straight from the stored bytes in one pass (field
names matched as UTF-8 literals, unmapped fields skipped undecoded) instead of materializing a `Document` first.
Typed `FindById`, and `Find`/`FindOne` without sort or projection, use it; with sort or projection they fall back
to `FromDocument`. Conversions, defaults for missing fields and "first occurrence wins" for duplicated names are the
same as `FromDocument`. Hand-written `IFolioDocument<T>` implementations inherit a default `FromView` that
materializes and calls `FromDocument`. `FromView` can also be called inside `TryReadById`/`Visit` callbacks.
Because typed reads map inside that borrowed read, mappers (and property setters/constructors they call) must not
write to the database; such writes throw `InvalidOperationException`, as in any borrowed callback.

For writes it emits `WriteTo(T, DocumentWriter)`, which serializes the members straight into the stored format.
Typed `Insert`/`InsertMany` use it (before the write transaction starts, like `ToDocument` did) and store exactly
the bytes `Insert(ToDocument(x))` would, including a generated `ObjectId` written first when the entity has no
`_id`. Typed `Update` uses it too: it looks the document up by primary key and stores the new bytes as they are when
they start with the stored `_id` (same type and bytes), which is exactly what `ReplaceOne({_id}, ToDocument(x))` would
store; otherwise (an `_id` stored later in the document or with another numeric type, a top-level `$` field name, an
`_id` that reads as an operator document, a serialization error) it falls back to that `Document` path. Hand-written implementations inherit a default `WriteTo` that
writes `ToDocument(value)`; an override must write each field once (a repeated top-level `_id` is rejected).

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
| Documents | `Documents/*` | `Document`/`DocValue` model, binary serializer, `RawDocument` zero-copy reader (public `DocumentView` wrappers), relaxed JSON (`ObjectId()`, `ISODate()`, single quotes). |
| Key encoding | `KeyEncoder.cs` | Order-preserving, memcmp-comparable encoding of any value (type rank + big-endian/escaped payload), so B+Trees compare raw bytes. Numbers of every type share one exact encoding: nearest double + integer remainder (+ 128-bit fraction only for non-double-representable decimals), computed with `Int128` arithmetic. |
| Pager + WAL | `Storage/Pager.cs`, `StorageTx.cs` | Fixed-size pages, page cache, WAL frames with salts + cumulative checksums; commit = commit frame (+ fsync). Recovery replays only fully committed, checksum-valid frames. Readers pin a WAL snapshot (`mxFrame`). |
| B+Tree | `Storage/BTree.cs` | Variable-length keys/values, overflow pages for large documents, copy-on-write via the transaction page set. Splits are balanced, except an insert past the last key of the rightmost leaf, which starts a new leaf (SQLite-style quick balance) so ascending keys fill pages. |
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

### B+Tree occupancy under churn

Issue #1 has a separate deterministic diagnostic entry point:

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --fragmentation 3000 4 3 > fragmentation.csv
```

It uses 4 KiB pages, a 1,024-page cache, `SynchronousMode.Normal`, disabled automatic checkpoints, three fresh
database trials and four churn cycles for both the default policy and an internal leaf-rebalance experiment. Policy
order alternates by trial and both policies use the same seed. Each trial starts with 3,000 documents, a simple `city`
index, compound `{bucket:1,score:-1}` index and multikey `tags` index. One eighth of documents use overflow values.
Every cycle interleaves deletes of 10% from a concentrated key interval and 10% selected randomly, inserts the same
number, then swaps equal numbers of inline and overflow documents while changing all indexed keys. Thus document
count and the approximate inline/overflow population stay stable. Integrity checks, checkpoints and all storage
statistics run outside timed sections; every captured stage has an empty WAL (32-byte header). `DELETE` rows capture
per-call p50/p95/p99, transaction commit time and WAL frames before that checkpoint.

For slotted B+Tree pages, `live` is the 12-byte header, two bytes per slot and current cell bytes; `fragmented` is
deleted cell space recorded inside the page; `free` is the remaining immediately usable capacity. These three values
sum exactly to page count × page size. Overflow `live` includes its four-byte next-page link, and overflow `free` is
unused tail capacity. `runs`, page-number span and maximum page-number gap describe allocator/page-number dispersion
only: they are not filesystem extents and do not prove an I/O-locality cost.

Results collected on 2026-09-30 (independent per-column medians of three trials; byte columns are median bytes).
Each individual diagnostic snapshot satisfies the accounting invariant above. These aggregate rows can combine
values from different trials, so their byte columns need not sum to the median page count times page size.

| Tree/stage | Height | Leaf / interior / overflow pages | Leaf live / fragmented / free | Interior live / fragmented / free | Overflow live / free | Runs / span / max gap |
|---|---:|---:|---:|---:|---:|---:|
| Primary baseline | 3 | 512 / 7 / 1,125 | 1,251,702 / 0 / 845,450 | 13,370 / 0 / 15,302 | 3,483,308 / 1,124,692 | 183 / 1,885 / 6 |
| Primary final | 3 | 686 / 12 / 1,182 | 1,187,243 / 741,576 / 882,895 | 17,954 / 5,424 / 25,774 | 3,647,506 / 1,193,966 | 272 / 2,236 / 6 |
| Simple baseline | 2 | 39 / 1 / 0 | 117,468 / 0 / 42,276 | 1,380 / 0 / 2,716 | 0 / 0 | 38 / 1,885 / 280 |
| Simple final | 2 | 48 / 1 / 0 | 117,576 / 38,073 / 40,959 | 1,704 / 0 / 2,392 | 0 / 0 | 46 / 1,998 / 221 |
| Compound baseline | 2 | 64 / 1 / 0 | 201,768 / 0 / 60,376 | 3,918 / 0 / 178 | 0 / 0 | 51 / 1,310 / 487 |
| Compound final | 3 | 78 / 3 / 0 | 201,936 / 50,635 / 66,917 | 4,810 / 0 / 7,478 | 0 / 0 | 65 / 1,977 / 378 |
| Multikey baseline | 3 | 134 / 3 / 0 | 319,858 / 0 / 229,006 | 4,691 / 0 / 7,597 | 0 / 0 | 117 / 1,820 / 139 |
| Multikey final | 3 | 222 / 4 / 0 | 331,010 / 289,692 / 293,714 | 7,783 / 0 / 8,601 | 0 / 0 | 191 / 2,218 / 91 |

Final leaf capacity was `live / fragmented / free`: primary 42.2% / 26.4% / 31.4%, simple 59.8% / 19.4% /
20.8%, compound 63.2% / 15.8% / 20.9%, and multikey 36.2% / 31.7% / 32.1%. The compound tree also grew from
height two to three with unchanged 3,000 entries. The catalog stayed unchanged at one leaf page
(327 live, 0 fragmented, 3,769 free bytes).

Freelist reuse worked but did not prevent retained growth:

| Stage | Total pages | Free pages | Main file |
|---|---:|---:|---:|
| Baseline | 1,888 | 0 | 7,733,248 B |
| Cycle 1 delete / insert / update | 1,888 / 1,965 / 1,990 | 272 / 0 / 0 | 7,733,248 / 8,048,640 / 8,151,040 B |
| Cycle 2 delete / insert / update | 1,990 / 2,048 / 2,054 | 282 / 0 / 0 | 8,151,040 / 8,388,608 / 8,413,184 B |
| Cycle 3 delete / insert / update | 2,054 / 2,144 / 2,152 | 277 / 0 / 0 | 8,413,184 / 8,781,824 / 8,814,592 B |
| Cycle 4 delete / insert / update | 2,152 / 2,228 / 2,238 | 274 / 0 / 0 | 8,814,592 / 9,125,888 / 9,166,848 B |

Matched probes used 2,000 random ID reads, 300 compound indexed ranges and 200 same-size writes. Trial values
(µs/op, baseline → final) were: ID `21.182/10.116/14.467 → 10.309/10.521/7.300`, range
`42.188/13.804/7.126 → 13.778/14.379/7.233`, and write
`138.716/35.858/33.300 → 37.842/29.264/40.520`. Medians were 14.467 → 10.309, 13.804 → 13.778 and
35.858 → 37.842 µs/op respectively. This shared host is noisy and baseline always precedes final state, so these
trials establish no defensible latency improvement or regression; they only show no large, repeatable timing cliff in
this bounded run. Occupancy/page counts are deterministic structural evidence and are the basis for the recommendations:

- **Delete merge/rebalance (#4): evaluated, not enabled by default.** Partial leaves persist across every tree; primary
  and multikey leaves end with only 42.2% and 36.2% live occupancy, and the compound tree gains a level. The bounded
  internal experiment below establishes that byte-based leaf merge/redistribution can recover meaningful space, but
  does not yet establish a tail-latency win. Keep the default empty-child-only policy and prefer explicit maintenance
  until representative delete-heavy workloads justify automatic work.
- **RebuildIndex (#3): second.** It is a useful explicit recovery path for the measured 15.8–31.7% secondary-leaf
  fragmentation and for the compound tree's extra level, but it only repairs one secondary tree at a time and does
  not prevent recurrence. The design needs a temporary root, atomic catalog-root swap, uniqueness validation, failure
  cleanup and an explicit decision between blocking rebuild and snapshot-based online rebuild with write catch-up.
- **Vacuum (#2): third, after merge and rebuild.** Deletes exposed 272–282 reusable pages and subsequent work consumed
  them, yet the stable-size workload still retained 350 extra pages (1.43 MiB) by the end. A vacuum must relocate live
  pages and truncate the tail; checkpoint/freelist cleanup alone cannot do that. Decide whether the first version is
  an offline atomic rewrite to a replacement file (simpler crash safety, requires temporary disk headroom) or an
  in-place page mover (smaller peak space, substantially harder WAL/snapshot/root-reference handling).

#### Issue #4 leaf merge/redistribution experiment

The internal-only candidate triggers when a non-root leaf falls below 30% live bytes. It merges with a sibling only
when the combined leaf remains at or below 75%, leaving split/merge hysteresis; otherwise it redistributes cells by
bytes and replaces the parent separator with the exact first key of the right leaf. The root page number remains
stable. Overflow chains are not copied, and all page rewrites/frees remain transaction-local until the existing WAL
commit. Interior-page underflow is deliberately deferred, so this is not a complete B+Tree compactor.

Median final structure across the three matched trials:

| Tree | Default leaf pages / live / fragmented | Candidate leaf pages / live / fragmented |
|---|---:|---:|
| Primary | 686 / 42.3% / 26.4% | 597 / 48.5% / 18.5% |
| Simple | 48 / 59.8% / 19.4% | 46 / 62.4% / 18.7% |
| Compound | 78 / 63.2% / 15.8% | 75 / 65.7% / 14.6% |
| Multikey | 222 / 36.4% / 31.8% | 148 / 54.5% / 17.2% |

The final database retained 2,071 pages (8,482,816 B) instead of 2,238 pages (9,166,848 B): 167 fewer pages,
684,032 B or 7.5%. Across the 12 matched delete stages, the candidate wrote 9,649 WAL page images versus 9,573
(+0.8%) and exposed 3,731 freelist pages versus 3,316 (+12.5%). Median stage values were 814.5 versus 799 page
images and 310 versus 273.5 pages freed.

Delete-call timing remained too noisy for a production decision on the shared host even with rollback warmup and
alternating policy order. Medians across the 12 stages (candidate versus default) were p50 11.75 versus 9.65 us,
p95 25.2 versus 23.6 us, p99 57.3 versus 49.5 us and commit 5,192.8 versus 4,474.3 us, while paired ratios varied
widely and aggregate elapsed time reversed direction because of host outliers. Matched final probes likewise showed
no repeatable query-latency effect. The candidate therefore remains an internal benchmark/test hook: the structural
benefit is valid for this seed, but automatic delete-path work is not promoted without broader latency evidence and
interior rebalancing.

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

#### Integrated FIFO comparison

FIFO is now available via `FolioOptions.WriterAdmission`; the benchmark above retains the external prototype
as a reference and adds a third CSV admission label, `integrated`, alongside `direct` and `fifo`.
The production implementation still uses monitor/PulseAll admission, not a different targeted-wakeup algorithm.
It additionally handles zero/infinite timeouts and interrupted waiters and replaces the engine semaphore rather
than wrapping it. Existing measurements above are historical external-gate results.

Promotion run, same Full/single-update workload and three interleaved 3-second repetitions on the shared host:

| Writers / readers | Admission | Updates/s | Successful update p99 | Timeouts (3 trials) |
|---|---|---:|---:|---:|
| 4 / 4 | Default | 436 | 251.7 ms | 8 |
| 4 / 4 | External FIFO | 418 | 26.3 ms | 0 |
| 4 / 4 | Integrated FIFO | 435 | 17.7 ms | 0 |
| 16 / 4 | Default | 357 | 73.7 ms | 119 |
| 16 / 4 | External FIFO | 283 | 105.3 ms | 0 |
| 16 / 4 | Integrated FIFO | 279 | 128.4 ms | 0 |

Integrated FIFO had zero admission timeouts in all measured scenarios; default had 269 across the complete
matrix. In the 16-writer/mixed scenario, median per-trial writer min/max counts were 1/439 for default and
53/54 for integrated FIFO. The successful-operation p99 alone is misleading when stalled default writers
end in a timeout and are excluded from that percentile. The integrated and external throughput medians were
similar under contention, but shared-host variation remains substantial (including uncontended runs);
these results do not establish exact overhead or a universal latency improvement. Default admission is unchanged
unless the caller explicitly opts in.

### Diagnostic captures of writer admission

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --profile-writers direct 120
dotnet run -c Release --project bench/FolioDb.Bench -- --profile-writers integrated 120
```

Run these **sequentially**, not together. This bounded profiling entry point selects Full, 16 writers, 4 readers,
one file and one update per transaction. It performs a discarded three-second warmup, creates a fresh database,
then prints the actual managed PID and UTC `LOAD_START`/`LOAD_END` markers. The trailing row uses the same CSV
column order as `--fairness`. Durations from 5 to 300 seconds are accepted; use 15 seconds for an uninstrumented
reference and a longer run to allow attachment. Attach only to the printed PID, after `LOAD_START` and a few
seconds of settling; keep the entire capture before `LOAD_END`. Setup and final integrity scans must not be
mistaken for workload activity. The counters/latencies in the trailing row span the entire run, not just capture
windows, and should not be used to estimate profiling overhead from runs of different duration.

On 2026-09-29, `dotnet-diagnostics` MCP captured counters, GC, CPU samples and managed contention concurrently
in one ten-second batch per policy, followed by separate ten-second allocation samples. A further FIFO
counters+GC-only capture cross-checked allocation/GC without simultaneous CPU/contention sampling.
Only synthetic benchmark data was used; no dumps, heap walks, method parameters or process suspension were requested.
The target advertised no off-CPU/native-lock sampling capability, so no OS scheduler/I/O wait decomposition was
attempted. All runs completed their per-document counter and integrity checks.

| Evidence in ten-second batched window | Default | Integrated FIFO |
|---|---:|---:|
| UTC window start | 23:42:28 | 23:44:20 |
| GC collections | 1,171 | 1,326 |
| Gen2 collections | 6 | 5 |
| Collector-reported total GC pause | 988.6 ms | 817.7 ms |
| Maximum GC pause | 16.2 ms | 9.4 ms |
| Managed contention events | 13,079 | 45,404 |
| Summed contention durations across threads | 4.219 s | 39.729 s |
| Last allocation-rate counter interval (~1 s) | ~1.87 GB/s | ~2.04 GB/s |

Contention duration sums can exceed wall time because multiple threads wait concurrently. They do not measure
whole writer-queue residence time and cannot be compared directly to our acquisition histograms. Call-site
drilldowns returned `(unknown)` and retained only the first 200 detailed rows, so these captures cannot attribute
the increase specifically to `WriterLock`, pager or cache. More contention is consistent with FIFO's PulseAll
handoff, but is not proof that PulseAll alone explains its throughput cost.

CPU-sample summaries attributed roughly 65% of Default samples to `SemaphoreSlim.WaitUntilCountOrTimeout` and 66%
of FIFO samples to `WriterLock.Wait`. The latter resolved to the `Monitor.Wait` line. The tool labeled these as
running/self CPU and produced a "cpu-bound" verdict, but that classification is insufficient to distinguish
blocked managed frames from actual on-CPU work here. **These percentages must not be reported as CPU consumed by
the locks.** FSync also appeared in Default stacks, without establishing how much wall time disk synchronization
consumed. CPU usage counters were only ~18%/~21% of the reported 16-processor capacity in the final intervals.

AllocationTick sampling highlighted `Byte[]`, `String`, read delegates and paths through `BTree.TryGet`,
`RawDocument.ToDocument` and the benchmark's `ReadLoop` in both variants. These are sampled allocation weights,
not an exact object census or trustworthy exact byte totals per type; inlining and allocation-tick attribution
can skew type/site shares. Runtime allocation-rate counters independently support heavy allocation pressure.
The FIFO counters+GC-only window reported ~2.59 GB in its last ~1-second interval and 1,049.8 ms summed GC pause
over ten seconds (maximum 4.9 ms), supporting the signal without simultaneous CPU/contention sampling.
This is allocation **turnover**, not evidence of a memory leak; the workload materializes full 1 KiB documents.

Uninstrumented 15-second references gave Default 436 updates/s, 588k reads/s and 178 one-second acquisition
timeouts; FIFO gave 220 updates/s, 534k reads/s and zero timeouts. The separate 120-second processes that hosted
captures gave Default 296 updates/s and 1,538 timeouts, versus FIFO 284 updates/s and zero timeouts. These are
single runs on a variable shared host with different durations and collector schedules, not a controlled estimate
of profiler overhead or a reversal of the earlier throughput comparison.

Next evidence-backed candidates are reducing full-read allocation/copying and experimenting with targeted FIFO
wakeups instead of PulseAll. Neither was changed by this diagnostic task. Quantifying fsync versus scheduler
waiting still requires a suitable off-CPU capture; these results alone do not justify changing durability.

### Single-page overflow read allocation

Following the diagnostic captures, reads of values that fit in one overflow page now borrow a span of that
page instead of allocating and copying a temporary byte array. This is the same internal lifetime rule as
inline values: consume the span before mutating the transaction. The public API still materializes independent
documents, including owned binary buffers and nested values. Multi-page overflow assembly is unchanged.
The shared B+Tree value reader covers both point lookups and cursor-based queries; there is no file-format,
durability or writer-admission change.

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --filter '*PointReadBenchmarks*' --job short
```

This benchmark uses 10,000 documents, 256 hot IDs, 4 KiB pages, no secondary indexes, and fully materialized
reads. Payloads are ASCII strings; 128 bytes stays inline, 1,024 bytes uses one overflow page, and 8,192 bytes
uses multiple pages. Each case warms the hot IDs after checkpointing and measures either a new implicit
transaction per read or an existing snapshot. On the same shared Linux/.NET 10 JIT host:

| Payload | Read scope | Allocated before | Allocated after |
|---|---|---:|---:|
| 128 B | Implicit transaction | 1,624 B/op | 1,624 B/op |
| 128 B | Existing snapshot | 768 B/op | 768 B/op |
| 1,024 B | Implicit transaction | 4,504 B/op | 3,416 B/op |
| 1,024 B | Existing snapshot | 3,648 B/op | 2,560 B/op |
| 8,192 B | Implicit transaction | 26,008 B/op | 26,008 B/op |
| 8,192 B | Existing snapshot | 25,152 B/op | 25,152 B/op |

The 1 KiB payload saves **1,088 bytes per read**, reducing allocation by 24.2% with an implicit transaction
or 29.8% with an existing snapshot. Its short-run means were 1.719 -> 1.687 us and 1.068 -> 0.994 us respectively,
but confidence intervals overlap: this establishes an allocation reduction, not a reliable latency gain.
The unchanged multi-page control also had a large timing swing between runs, reinforcing shared-host noise.
These are per-operation allocation measurements, not predicted concurrent throughput or measured GC-pause
reductions. Strings, document objects, field names and transaction/delegate allocations remain.

### Borrowed point-read allocation

`PointReadBenchmarks` (same data set and hosts as above) now also computes one comparable scalar per read,
`n + payload length`, either from `FindById` (materialized `Document` and payload string) or from `TryReadById`
with a `static` lambda. The existing `FindById` rows were re-measured on the unchanged baseline commit and after this
change to confirm the materialized path is unaffected (1,624 / 768 / 3,416 / 2,560 / 26,008 / 25,152 B/op both times).
Short job, one launch, three iterations, shared Linux/.NET 10 JIT host:

| Payload | Read scope | Materialized scalar | Borrowed scalar | Allocation saved |
|---|---|---:|---:|---:|
| 128 B | Implicit transaction | 1,624 B/op | 904 B/op | 44% |
| 128 B | Existing snapshot | 768 B/op | **48 B/op** | 94% |
| 1,024 B | Implicit transaction | 3,416 B/op | 904 B/op | 74% |
| 1,024 B | Existing snapshot | 2,560 B/op | **48 B/op** | 98% |
| 8,192 B | Implicit transaction | 26,008 B/op | 9,160 B/op | 65% |
| 8,192 B | Existing snapshot | 25,152 B/op | 8,304 B/op | 67% |

At this stage, the remaining 48 bytes were the encoded id key. The implicit scope additionally allocated its read
transaction and per-transaction catalog lookup (856 B). The subsequent setup optimization below removes that key
array and reduces catalog allocations. Multi-page (8 KiB) documents still assemble one temporary buffer (~8.2 KB).
Short-run means moved in the same direction (for example 705 -> 514 ns for 128 B and 990 -> 526 ns for 1 KiB in a
snapshot, 4,349 -> 2,427 ns for 8 KiB implicit), but the three-iteration intervals are wide on a shared host;
treat them as indicative, not as a latency guarantee. The gain depends on how much of the document the caller would
otherwise materialize, and callers that need the whole document still pay for `ToDocument()`.

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --filter '*PointReadBenchmarks*' --job short
```

### Borrowed query allocation

`VisitBenchmarks` compares the same numeric sum over 1, 100 and 1,000 indexed matches in a 10,000-document
collection with 1 KiB ASCII payloads. All three paths use implicit read transactions and the same secondary
index on `bucket`; the projected result contains only `n` (not covered by that index). The visitor delegate is
cached during setup. Warm reads, BenchmarkDotNet ShortRun, one launch/three iterations, shared Linux/.NET 10 JIT:

| Matches per query | Find: full documents | Find: projected `n` | Visit: borrowed |
|---|---:|---:|---:|
| 1 | 6.22 KiB/query | 4.59 KiB/query | 3.63 KiB/query |
| 100 | 241.85 KiB/query | 26.76 KiB/query | 3.63 KiB/query |
| 1,000 | 2,379.36 KiB/query | 223.64 KiB/query | 3.63 KiB/query |

In this warm, single-overflow-page workload, visitor allocations did not grow with result count. At 1,000
matches this is about 99.85% less allocation than full materialization and 98.4% less than projection.
This is not a zero-allocation guarantee for other plans, filters or document sizes.
Mean times at 100 matches were 86.1 / 48.6 / 34.0 us, respectively; these short shared-host measurements
are indicative, not latency guarantees or concurrency measurements.

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --filter '*VisitBenchmarks*' --job short
```

### Read setup optimizations: key, catalog, prepared filter

These changes were measured sequentially, before moving to the next step, on the same shared Linux/.NET 10 JIT
host with BenchmarkDotNet ShortRun. Earlier tables above describe their historical stage.

1. Point lookups now consume `KeyEncoder`'s existing per-thread buffer directly in the synchronous B+Tree lookup.
   Nothing keeps that key span past the lookup or across a callback. Persistent index/filter keys still use owned
   arrays and the exact same encoding (including cross-type numeric equality).
2. Catalog reads decode metadata directly from binary views instead of building intermediate documents, arrays and
   field-name strings. Names and index descriptors that must persist are still owned. The catalog cache remains
   per transaction; the on-disk format and catalog validation rules are unchanged.
3. Optional prepared filters reuse the existing predicate tree, not a stale catalog-dependent plan.

| Warm borrowed point read, 1 KiB payload | Before | After key | After catalog |
|---|---:|---:|---:|
| Existing snapshot | 48 B/op | **0 B/op** | **0 B/op** |
| Implicit transaction | 904 B/op | 856 B/op | **472 B/op** |

The 0 B/op measurement is for a warmed cache and encoding buffer with numeric ids and a cached static callback,
not a blanket promise. The 8 KiB multi-page case still allocated 8,256 B/op in a snapshot after removing the key
array; its implicit read dropped from 9,160 to 8,728 B/op after both optimizations. Materialized point reads also
benefit from removing the key array; materialization itself remains unchanged.

For the indexed query benchmark (1, 100 or 1,000 matches), borrowed queries allocated 3.63 KiB/query originally,
2.49 KiB after direct catalog decoding, and **1.96 KiB with a reused prepared filter**. In the final comparison:

| 1 match per query | Document filter | Prepared filter |
|---|---:|---:|
| Full materialization | 5.08 KiB | 4.55 KiB |
| Projection | 3.45 KiB | 2.92 KiB |
| Borrowed visitor | 2.49 KiB | 1.96 KiB |

Preparation happened in benchmark setup and the visitor delegate was cached. These savings are per query,
not per result: with 1,000 matches, borrowed allocations remained 2.49 vs 1.96 KiB in this workload.
At one match the visitor means were 2.628 vs 2.349 us; at 100 matches they were 34.245 vs 33.604 us with overlapping
intervals. Shared-host noise and short iterations prevent broad CPU/latency claims, particularly for larger
result sets where traversal dominates. No concurrent-throughput or profiler-overhead claim is inferred.

### Profiling prepared-filter Visit (1 vs 1,000 matches)

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --profile-visit 1 60
dotnet run -c Release --project bench/FolioDb.Bench -- --profile-visit 1000 60
```

This bounded entry point builds the `VisitBenchmarks` data set (10,000 documents, 1 KiB payload, index on `bucket`,
1/100/1,000 matches), then repeats one warm, read-only, single-threaded `Visit(PreparedFilter)` that sums `n`. Every
query checks its visit count and exact sum. It warms for three seconds, then prints the actual PID and UTC
`LOAD_START`/`LOAD_END` markers; setup, checkpoint and cleanup stay outside that window. The trailing CSV row is
queries/s and us/query over the whole window (5-300 s accepted). Run captures **sequentially**, one process each.

On 2026-09-30, `dotnet-diagnostics` MCP took ten-second CPU samples inside 60-second windows at 1 and 1,000
matches, plus a ten-second allocation sample at one match. No dumps, heap walks, parameter capture or suspension
were requested; the target reported no off-CPU capability. Findings, with caveats:

- **1,000 matches** (7,398 samples): `QueryPlanner.Execute` self 43.5% (the cursor loop; intermediate frames such
  as the engine callback and `FieldFilter.Matches` were inlined or missing from stacks), filter re-evaluation
  (`FieldFilter.Walk`, re-encoding each value for comparison) 12.4% inclusive, primary `SeekExact` 10.8%, overflow
  `Pager.ReadPage` ~9%, `DocumentView.TryGetValue` 4.7%. The index plan is *covered* (non-multikey, single equality),
  so that filter re-evaluation was redundant: `Count` already trusts the same plan without reading documents.
- **1 match**: 99.6% of CPU samples landed in `Thread.PollGCWorker` via `BulkMoveWithWriteBarrier` in
  `OrderedIterator.MoveNext`, i.e. the per-execution `plan.Keys.OrderBy(...)`. That share is a safe-point sampling
  artifact, **not** CPU cost (the same site also took 14.5% of the 1,000-match samples); it only shows that each query
  sorted keys the planner had already sorted.
- **Allocation** (sampled AllocationTick weights, not an exact census): `OrderBy` machinery (`OrderedIterator`,
  `EnumerableSorter`, `Int32[]`, `Comparison<int>`, part of `Byte[][]`) was about 18% of sampled bytes and the
  planner's LINQ closures (`ListWhereIterator`, `Func<IndexMeta,bool>`) about 7%. Cursor stacks, transaction and
  per-transaction catalog objects make up most of the rest.

Changes: equality/`$in` plan keys are sorted and deduplicated once by the planner (already an invariant) and scanned
directly; simple-index lookup uses loops instead of LINQ closures; and `Visit`/`Find` skip re-evaluating the filter
for covered plans, exactly as `Count` already did. Indexed plans are covered only when their bounds exactly
decide the filter; multikey secondary indexes remain non-covered. Primary-key plans and unfiltered scans can also be covered;
numeric cross-type equality, type bracketing and multikey/snapshot behavior are covered by regression tests
comparing indexed and unindexed collections.

BenchmarkDotNet ShortRun (`--filter '*VisitBenchmarks.*Prepared*' '*VisitBenchmarks.Borrowed*' --job short`),
before -> after on the same shared host:

| Case | Matches | Mean before | Mean after | Allocated before | Allocated after |
|---|---:|---:|---:|---:|---:|
| Visit, prepared | 1 | 2.538 us | 2.536 us | 1.96 KB | **1.51 KB** |
| Visit, prepared | 100 | 35.502 us | 23.674 us | 1.96 KB | 1.51 KB |
| Visit, prepared | 1,000 | 334.815 us | 258.715 us | 1.96 KB | 1.51 KB |
| Visit, document filter | 1 / 100 / 1,000 | 4.328 / 35.460 / 325.502 us | 2.457 / 24.752 / 239.017 us | 2.49 KB | 2.04 KB |
| Find, prepared, full documents | 1,000 | 946.8 us | 996.0 us | 2,377.69 KB | 2,377.23 KB |

Allocation drops by ~460 bytes per query for every result size. Because ShortRun intervals were wide (for example
+-213 us at 1,000 matches), timing was cross-checked with the profile runner, interleaving HEAD and the change
(three rounds, ten seconds each, one-minute host load 1.6-2.7): **2.38-2.47 -> 2.01-2.04 us** per one-match query
(-16%) and **327-330 -> 215-231 us** per 1,000-match query (-32%). Materialized `Find` remains dominated by document
construction and did not measurably change. A post-change CPU sample at 1,000 matches showed no filter or `OrderBy`
frames; remaining samples are attributed to the cursor loop, page reads (14.8%), key comparisons (12.5%) and the callback's field
lookup (8.6%). These are single-thread, warm, JIT measurements on a variable shared host (an unrelated compile
briefly pushed load above 50 and invalidated one run, which was discarded), not AOT, cold-cache or concurrent results.

Next candidates, not changed here: per-query cursor/transaction/catalog allocations, overflow-page lookup cost for
1 KiB documents, and callers' repeated UTF-8 transcoding of field names in `TryGetValue`.

### Index maintenance allocations on writes

```sh
dotnet run -c Release --project bench/FolioDb.Bench -- --filter '*WriteBenchmarks*' --job short
```

`WriteBenchmarks` inserts, replaces and deletes 1,000 documents in a single transaction with 0, 1 (simple) or 2
(simple plus compound) secondary indexes. `InsertNoIndexWork` repeats the inserts without creating the indexes, so
the difference isolates what index maintenance costs per document.

Three changes were measured together, since they all target the same per-document path:

1. `IndexMeta` decodes each indexed path once into UTF-8 segments (and array positions) and caches them with the
   rest of the cached catalog entry, instead of splitting the string and transcoding every segment for every
   indexed document. `FieldsInOrder` uses the same segments.
2. Compound keys are built depth-first into two pooled buffers, so each produced key allocates its final key and
   hint once rather than one intermediate array per path prefix at every level.
3. Index entries (`value ++ id` and `idHint ++ valueHint`) are written from pooled buffers. The B+Tree already
   took spans and copies them into the page, so the per-entry arrays were pure garbage. The sorted bulk-load paths
   (`RebuildIndex`, `VacuumInto`) keep owned arrays, because they retain entries until the sort completes.

| Allocated per 1,000 documents | Before | After |
|---|---:|---:|
| Insert, no secondary index | 2.20 MB | 2.17 MB |
| Insert, 1 index | 3.09 MB | **2.87 MB** |
| Insert, 2 indexes | 4.89 MB | **4.01 MB** |
| Delete, 1 index | 3.31 MB | **3.09 MB** |
| Delete, 2 indexes | 5.21 MB | **4.36 MB** |
| Replace, 2 indexes | 5.29 MB | **5.02 MB** |

Expressed as index maintenance alone (the gap to `InsertNoIndexWork`), the simple index went from 890 to 700
bytes per document (-21%) and the pair from 2,690 to 1,840 bytes (-32%). The remaining base cost is dominated by
building and serializing the document and by the B+Tree's copy-on-write pages, neither of which changed.

ShortRun timing on this shared host was inconclusive for these benchmarks: with one invocation per iteration and
three iterations, the reported error reached +-54 ms on a ~31 ms mean, so no CPU claim is made here. The
allocation column is reproducible across runs and is the only result claimed. `Replace` and `Delete` also pay for
the query that locates the document (visible as the 0-index rows costing more than `Insert`), which this change
did not touch.

### Replacement updates

A replacement update (`UpdateOne(filter, wholeDocument)`, as opposed to an operator update such as `$set`) used to
decode the stored document into a `Document` only to read its `_id` back out, then throw that document away and
serialize the replacement. `UpdateApplier.ApplyReplacement` now reads `_id` directly from the stored bytes and
`DocumentSerializer.SerializeWithId` writes it as the first field, skipping any `_id` carried by the replacement.
The `_id`-is-immutable rule and the resulting field order are unchanged, because `Document.InsertFirst` already
moved `_id` to position 0. `Collection.Update` also decides operator-versus-replacement once instead of per matched
document.

Measured inside an explicit transaction on a collection of 1,000 documents with no secondary indexes, `ReplaceOne`
went from 4,443 to 3,059 bytes per operation (-31%); of that, 934 bytes are the replacement document built by the
caller, so the engine's own share fell from 3,509 to 2,125 bytes (-39%).

### Per-query allocations on reads

An allocation sample of a `Count`-by-`_id` loop attributed 25% of bytes to `Collection.Read` itself (the implicit
transaction), 24% to `FilterParser.ParseField` and 11% to `QueryPlanner.Plan`. Two of the named contributors were
removed:

1. `FieldPath` — the dotted-path decoder introduced for index metadata — is now shared with `FieldFilter`, which
   was splitting its path and transcoding every segment through two LINQ `Select` projections each time a filter
   was parsed. The per-filter `Func`, iterator and array garbage is gone; the work is a single loop.
2. `EngineTx` keeps its first catalog entry in two fields and only allocates the backing `Dictionary` when a second
   collection name is requested in the same transaction. Almost every transaction touches one collection, so the
   dictionary and its entry array were allocated and discarded per query.

| Allocated per operation (implicit transaction, 1,000 documents) | Before | After |
|---|---:|---:|
| `Count` by `_id` | 1,688 B | **1,360 B** |
| `FindById` | 1,656 B | **1,456 B** |
| `Find` by unindexed field, limit 1 | 3,067 B | **2,735 B** |

Building the filter document itself accounts for 208 B of each figure and is the caller's cost; `PreparedFilter`
avoids re-parsing for repeated queries. No timing claim is made — see the note above about this host.

### Where read time goes, and presized materialization

A CPU sample of a mixed read loop (20,000 documents; `FindById`, indexed `Find` with limit 20, and `Count` by `_id`)
put under 1% of samples in `QueryPlanner.Plan`, `BTree.Cursor.SeekExact` and `Pager.ReadPage` combined, and about
98% under materialization — `RawDocument.ToDocument` for `Find` and `DocumentSerializer.Deserialize` for
`FindById`. The sampler only landed on GC poll points, so it over-represents allocating code; read it as a
relative ranking, not as a CPU breakdown. Even so, it is why plan caching was not pursued: planning is not where
reads spend their time. Building owned `Document`s is, which is also what the borrowed `Visit`/`TryReadById`
APIs avoid.

The binary format does not store a field count, so materialization started from an empty list and grew it
(4, 8, 16... slots of 32 bytes each), discarding each outgrown array. `RawDocument.ToDocument` and
`RawArray.ToArray` now count elements first — walking only the type/name/length headers, decoding nothing — and
allocate the list at its final size. The count pass validates less than decoding does (decimals and nested
contents are only checked when materialized), so if it fails the list falls back to growing and materialization
reports the first error in field order, exactly as before.

| Allocated per operation (7 fields, one 3-item array, one 2-field subdocument) | Before | After |
|---|---:|---:|
| `FindById` | 2,056 B | **1,784 B** |
| `Find` by indexed field, limit 10 | 16,099 B | **13,374 B** |

That is about 272 bytes per materialized document. An interleaved A/B (six alternating runs of each build,
comparing minimums, host load about 5) found the timing neutral: -0.5% for `FindById` and -0.6% for the
limit-10 `Find`, within noise. The counting pass costs roughly what the avoided copies did.

### Typed reads without an intermediate `Document`

Typed reads used to pay twice: materialize a `Document` (every name and string decoded, every value boxed into a
list), then copy it into the entity. The generated `FromView` reads the entity from the borrowed bytes instead, so
only the entity's own strings and collections are allocated.

| Per operation (same 7-field documents as above) | Document | Typed, before | Typed, `FromView` |
|---|---:|---:|---:|
| `FindById` allocated | 1,784 B | 1,960 B | **920 B** |
| `Find` by indexed field, limit 10, allocated | 12,696 B | 15,096 B | **5,816 B** |
| `FindById` time (minimum of 7) | ~2.5 µs | ~2.5 µs | **~1.9 µs** |
| `Find` limit 10 time (minimum of 7) | ~11.5 µs | ~13.4 µs | **~8.2 µs** |

A hand-written single-pass reader for the same type allocated 928 B and took ~2.0 µs, so the generated code is at
that bound. Times come from three runs on a shared host (load about 4) and are relative, not guarantees; the
allocation figures are the reproducible ones. Typed reads are now cheaper than untyped `Document` reads.

### WAL commit without a frame buffer

Commit used to copy every dirty page into a single `pages × (24 + page size)` buffer, hash it and write it. With
~224 dirty pages per 1,000-insert transaction that was a ~920 KB large-object allocation per commit; in isolation
the allocation (with its zeroing and the gen2 collections it triggers) cost more than the copy and the hash
together. Commit now writes a small header buffer plus the page images themselves through one gather write
(`pwritev`), computing the same chained XxHash64 incrementally, so the WAL format is unchanged. Checkpoint
writes the latest image of each page from the page cache when it is still resident (reading the WAL otherwise)
and sends runs of consecutive page numbers in a single gather write.

| Per insert (typed, index on `city`, 1,000 per transaction, `SynchronousMode.Off`) | Before | After |
|---|---:|---:|
| Allocated | 5,368 B | **4,530 B** |
| Commit share (incl. automatic checkpoints) | ~2.5 µs | **~1.7 µs** |
| Insert + commit | ~6.6 µs | **~5.6 µs** |
| Gen2 collections in a 6 s loop | ~200 | **~55** |

Three runs of each build on a shared host (load 3-5); times are relative, the allocation figures reproducible.

### Packed leaves for ascending inserts

Every leaf split used to divide the cells evenly, so keys arriving in ascending order (sequential `_id`s,
timestamps) left each leaf half full and never touched again. When the new key goes past the last key of the
rightmost leaf, the full leaf is now kept as is and the new key starts the next leaf, as SQLite's quick balance
does. Other inserts still split evenly, so random and descending patterns are unchanged.

| 300,000 sequential typed inserts, 1,000 per transaction | Before | After |
|---|---:|---:|
| No secondary index: file size | 97.7 MiB | **51.5 MiB** |
| No secondary index: WAL pages per commit | 87.3 | **47.5** |
| Index on `city` (100 values): file size | 119.8 MiB | **73.5 MiB** |
| Index on `city`: WAL pages per commit | 216.1 | **176.2** |
| Insert + commit, with index (two runs) | ~4.3-4.5 µs | **~4.0-4.3 µs** |

The `city` index keys (`city`, `_id`) grow at the end of each city's range, not at the end of the tree, so that
index still splits evenly. Page and file counts are deterministic; times are relative to a shared host.

### One primary descent per insert

Insert checked for a duplicate `_id` with one B+Tree descent and then inserted with a second one. It now locates
the slot once (`BTree.TryLocateNew`), validates the secondary indexes (read-only, so the slot stays valid) and
inserts there (`InsertNew`). Error precedence is unchanged: a duplicate `_id` is still reported before index errors
and nothing is written when validation fails. In an interleaved A/B (minimum of three runs, 200,000 typed inserts,
commit excluded) the insert went from ~1.08-1.14 to ~0.99-1.01 µs with sequential `_id`s, ~5.3-5.8 to ~4.9-5.1 µs
with shuffled `_id`s and ~2.38-2.55 to ~2.23-2.33 µs with an index on `city`; relative figures on a shared host.

### Typed inserts without an intermediate `Document`

Typed inserts built a `Document` (a field list, a `DocArray` per collection, a `Document` per nested entity) only to
serialize it right away. The generated `WriteTo` writes the bytes directly. Same typed 7-field entity with a list and
a nested entity, 200,000 inserts in transactions of 1,000, commit excluded, interleaved A/B (minimum of four runs):

| Per insert | `ToDocument` | `WriteTo` |
|---|---:|---:|
| Allocated, no secondary index | 1,669 B | **1,093 B** |
| Allocated, index on `city` | 2,913 B | **2,337 B** |
| Time, no secondary index | ~1.24-1.33 µs | **~0.90-0.93 µs** |
| Time, index on `city` | ~2.87-2.96 µs | **~2.22-2.23 µs** |

Allocation figures are reproducible; times are relative to a shared host.

Typed `Update` (a whole-document replace by `_id`) takes the same route: one primary lookup, then the `WriteTo`
bytes are stored directly, instead of `ToDocument`, a filter, a planned match and a re-serialization with the stored
`_id`. Same entity shape, 20,000 updates per transaction: **2.6 KB → 1.05 KB** allocated per update without
secondary indexes and **4.6 KB → 3.0 KB** with an index on `city`.

## Limitations

- Single process per database file (exclusive file handles); concurrency is between threads of that process.
- No full-text search, aggregation disk spilling, joins, or general expression language.
- Inside an explicit transaction, a failing multi-document statement (`InsertMany`, `UpdateMany`, index backfill) dooms the transaction
  instead of rolling back only that statement (auto-commit mode rolls back the whole statement). A duplicate-key error on a single-document operation does not doom it.
- `TryReadById` and `Visit` are optional borrowed APIs; untyped `Find`, typed `Find` with sort or projection, and aggregation still materialize documents.
  Their scope guard detects same-thread reentrancy only (transactions and snapshots are single-threaded objects).
- Maximum document size is bounded by `CollectionEngine.MaxDocumentSize`; index keys must fit in a page fraction.
