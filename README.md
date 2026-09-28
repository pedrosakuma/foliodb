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
- **B+Tree** primary index on `_id` (auto-generated `ObjectId` when missing) and **secondary indexes** (single field, dotted paths, unique, multikey on arrays).
- **Mongo-style queries**: `$eq $ne $gt $gte $lt $lte $in $nin $exists $type $size $all $elemMatch $regex $not $and $or $nor`, sort, skip, limit, projection, `explain`.
- **Updates**: `$set $unset $inc $mul $min $max $rename $push($each) $addToSet $pull $pop $currentDate`, replace, upsert.
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
| Key encoding | `KeyEncoder.cs` | Order-preserving, memcmp-comparable encoding of any value (type rank + big-endian/escaped payload), so B+Trees compare raw bytes. |
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

## Limitations

- Single process per database file (exclusive file handles); concurrency is between threads of that process.
- Single-field indexes only (no compound indexes); no aggregation pipeline, no full-text search.
- Typed `decimal` members are stored losslessly as invariant strings, so range queries on them compare as strings; `ulong` values above `long.MaxValue` are stored the same way.
- Inside an explicit transaction, a failing multi-document statement (`InsertMany`, `UpdateMany`, index backfill) dooms the transaction
  instead of rolling back only that statement (auto-commit mode rolls back the whole statement). A duplicate-key error on a single-document operation does not doom it.
- Maximum document size is bounded by `CollectionEngine.MaxDocumentSize`; index keys must fit in a page fraction.
