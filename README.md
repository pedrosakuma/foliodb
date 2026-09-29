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

## Limitations

- Single process per database file (exclusive file handles); concurrency is between threads of that process.
- No aggregation pipeline or full-text search.
- Inside an explicit transaction, a failing multi-document statement (`InsertMany`, `UpdateMany`, index backfill) dooms the transaction
  instead of rolling back only that statement (auto-commit mode rolls back the whole statement). A duplicate-key error on a single-document operation does not doom it.
- Maximum document size is bounded by `CollectionEngine.MaxDocumentSize`; index keys must fit in a page fraction.
