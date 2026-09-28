using System.Diagnostics;
using System.Text;
using FolioDb;

namespace FolioDb.Cli;

/// <summary>
/// Interactive shell, in the spirit of the sqlite3 CLI, with a Mongo-shell-like syntax:
/// <code>db.users.find({ age: { $gt: 30 } }).sort({ name: 1 }).limit(10)</code>
/// </summary>
internal static class Program
{
    private const string Usage = """
        Usage: folio [options] <database-file> [command ...]

        Options:
          -c <command>   run a command and exit (may be repeated)
          -bail          stop at the first error
          -json          print each document as compact JSON (default when not a terminal)
          -pretty        print documents as indented JSON (default for interactive use)
          -h, --help     show this help

        Commands are read from stdin when it is not a terminal:  folio app.folio < script.js
        """;

    public static int Main(string[] args)
    {
        string? path = null;
        var commands = new List<string>();
        bool bail = false;
        bool? pretty = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "-help":
                    Console.WriteLine(Usage);
                    return 0;
                case "-c" when i + 1 < args.Length: commands.Add(args[++i]); break;
                case "-bail": bail = true; break;
                case "-json": pretty = false; break;
                case "-pretty": pretty = true; break;
                default:
                    if (path is null && !args[i].StartsWith('-')) path = args[i];
                    else if (path is not null) commands.Add(args[i]);
                    else
                    {
                        Console.Error.WriteLine($"Unknown option '{args[i]}'.\n\n{Usage}");
                        return 2;
                    }
                    break;
            }
        }
        if (path is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        bool interactive = commands.Count == 0 && !Console.IsInputRedirected;
        FolioDatabase db;
        try
        {
            db = FolioDatabase.Open(path);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }

        using var shell = new Shell(db, Console.Out, pretty ?? interactive);
        int errors = 0;
        if (commands.Count > 0)
        {
            foreach (var c in commands)
            {
                if (!shell.Execute(c)) errors++;
                if (errors > 0 && bail || shell.QuitRequested) break;
            }
        }
        else
        {
            if (interactive)
                Console.WriteLine($"FolioDb shell — {Path.GetFullPath(path)}\nEnter \".help\" for usage hints.");
            errors = shell.RunReader(Console.In, interactive, bail);
        }
        return errors > 0 ? 1 : 0;
    }
}

internal sealed class Shell : IDisposable
{
    private readonly FolioDatabase _db;
    private readonly TextWriter _out;
    private bool _pretty;
    private bool _timer;
    private Transaction? _tx;

    public Shell(FolioDatabase db, TextWriter output, bool pretty)
    {
        _db = db;
        _out = output;
        _pretty = pretty;
    }

    public bool QuitRequested { get; private set; }

    public void Dispose()
    {
        if (_tx is not null)
        {
            _out.WriteLine("Rolling back open transaction.");
            _tx.Dispose();
        }
        _db.Dispose();
    }

    /// <summary>Reads statements (possibly spanning lines) until EOF or .quit. Returns the number of failed statements.</summary>
    public int RunReader(TextReader reader, bool interactive, bool bail)
    {
        int errors = 0;
        var buffer = new StringBuilder();
        while (!QuitRequested)
        {
            if (interactive) _out.Write(buffer.Length == 0 ? (_tx is null ? "folio> " : "folio*> ") : "   ...> ");
            var line = reader.ReadLine();
            if (line is null) break;
            if (buffer.Length == 0 && line.TrimStart().StartsWith('.'))
            {
                if (!Execute(line)) errors++;
            }
            else
            {
                buffer.AppendLine(line);
                if (!IsComplete(buffer.ToString())) continue;
                var text = buffer.ToString();
                buffer.Clear();
                if (string.IsNullOrWhiteSpace(StripComments(text))) continue;
                if (!Execute(text)) errors++;
            }
            if (errors > 0 && bail) break;
        }
        if (buffer.Length > 0 && !string.IsNullOrWhiteSpace(StripComments(buffer.ToString())) && !Execute(buffer.ToString())) errors++;
        return errors;
    }

    private static string StripComments(string s) =>
        string.Join('\n', s.Split('\n').Select(l => l.TrimStart().StartsWith("//") ? "" : l));

    /// <summary>A statement is complete when brackets are balanced outside of string literals.</summary>
    internal static bool IsComplete(string s)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
        }
        return depth <= 0 && quote == '\0';
    }

    public bool Execute(string input)
    {
        var text = input.Trim();
        while (text.EndsWith(';')) text = text[..^1].TrimEnd();
        if (text.Length == 0) return true;
        var sw = Stopwatch.StartNew();
        try
        {
            if (text.StartsWith('.')) DotCommand(text);
            else Statement(text);
            if (_timer) _out.WriteLine($"Run Time: {sw.Elapsed.TotalMilliseconds:F3} ms");
            return true;
        }
        catch (Exception e) when (e is FolioException or FormatException or InvalidOperationException or ArgumentException
                                      or InvalidCastException or IOException or UnauthorizedAccessException or OverflowException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ statements

    private Collection Col(string name) => _tx is not null ? _tx.GetCollection(name) : _db.GetCollection(name);

    private void Statement(string text)
    {
        switch (text.ToLowerInvariant())
        {
            case "begin" or "begin transaction":
                if (_tx is not null) throw new InvalidOperationException("A transaction is already open.");
                _tx = _db.BeginTransaction();
                return;
            case "commit" or "end":
                (_tx ?? throw new InvalidOperationException("No open transaction.")).Commit();
                _tx = null;
                return;
            case "rollback":
                (_tx ?? throw new InvalidOperationException("No open transaction.")).Rollback();
                _tx = null;
                return;
            case "show collections" or "show tables":
                ListCollections();
                return;
            case "exit" or "quit":
                QuitRequested = true;
                return;
        }

        var calls = CallParser.Parse(text);
        if (calls.Count == 0 || calls[0].Name != "db") throw new FormatException("Statements start with 'db.' (try .help).");
        int i = 1;
        string? colName = null;
        if (i < calls.Count && calls[i].Name == "getCollection" && calls[i].Args is [{ Type: DocType.String } n])
        {
            colName = n.AsString;
            i++;
        }
        else if (i < calls.Count && calls[i].Args is null)
        {
            colName = calls[i].Name;
            i++;
        }

        if (colName is null)
        {
            if (i >= calls.Count) throw new FormatException("Incomplete statement.");
            DbMethod(calls[i]);
            return;
        }
        if (i >= calls.Count || calls[i].Args is null) throw new FormatException($"Missing method call on collection '{colName}'.");
        CollectionMethod(Col(colName), calls[i], calls.Skip(i + 1).ToList());
    }

    private void DbMethod(Call call)
    {
        switch (call.Name)
        {
            case "getCollectionNames": PrintValue(new DocArray(_db.GetCollectionNames().Select(n => (DocValue)n))); break;
            case "stats": Stats(); break;
            case "checkpoint": Checkpoint(); break;
            case "dropCollection": _out.WriteLine(_db.DropCollection(Arg(call, 0).AsString)); break;
            default: throw new FormatException($"Unknown database method '{call.Name}'.");
        }
    }

    private static DocValue Arg(Call c, int i) => c.Args!.Count > i ? c.Args[i] : DocValue.Null;

    private static Document? DocArg(Call c, int i)
    {
        var v = Arg(c, i);
        return v.Type switch
        {
            DocType.Null => null,
            DocType.Document => v.AsDocument,
            _ => throw new FormatException($"{c.Name}: argument {i + 1} must be a document."),
        };
    }

    private static Document RequiredDoc(Call c, int i) => DocArg(c, i) ?? throw new FormatException($"{c.Name}: argument {i + 1} is required.");

    private static bool Option(Call c, int i, string name) =>
        DocArg(c, i) is { } o && o.TryGetValue(name, out var v) && v.Type == DocType.Boolean && v.AsBoolean;

    private void CollectionMethod(Collection col, Call call, List<Call> modifiers)
    {
        if (call.Name != "find" && modifiers.Count > 0 && !(call.Name == "findOne" && modifiers.All(m => m.Name == "sort")))
            throw new FormatException($"'{modifiers[0].Name}' cannot be chained after {call.Name}().");
        switch (call.Name)
        {
            case "find":
            {
                Document? sort = null;
                int skip = 0;
                int? limit = null;
                bool count = false, explain = false;
                foreach (var m in modifiers)
                {
                    switch (m.Name)
                    {
                        case "sort": sort = RequiredDoc(m, 0); break;
                        case "skip": skip = Arg(m, 0).AsInt32; break;
                        case "limit": limit = Arg(m, 0).AsInt32; break;
                        case "count": count = true; break;
                        case "explain": explain = true; break;
                        case "pretty": break;
                        default: throw new FormatException($"Unknown cursor method '{m.Name}'.");
                    }
                }
                var filter = DocArg(call, 0);
                if (explain)
                {
                    _out.WriteLine(col.Explain(filter));
                    return;
                }
                if (count)
                {
                    _out.WriteLine(col.Count(filter));
                    return;
                }
                var docs = col.Find(filter, new FindOptions { Sort = sort, Skip = skip, Limit = limit, Projection = DocArg(call, 1) });
                foreach (var d in docs) PrintValue(d);
                if (_pretty) _out.WriteLine($"({docs.Count} document{(docs.Count == 1 ? "" : "s")})");
                break;
            }
            case "findOne":
            {
                var sort = modifiers.Count > 0 ? RequiredDoc(modifiers[^1], 0) : null;
                var d = col.FindOne(DocArg(call, 0), new FindOptions { Projection = DocArg(call, 1), Sort = sort });
                if (d is null) _out.WriteLine("null");
                else PrintValue(d);
                break;
            }
            case "count" or "countDocuments": _out.WriteLine(col.Count(DocArg(call, 0))); break;
            case "explain": _out.WriteLine(col.Explain(DocArg(call, 0))); break;
            case "insert" or "insertOne" or "insertMany":
            {
                var v = Arg(call, 0);
                if (v.Type == DocType.Array)
                {
                    var ids = col.InsertMany(v.AsArray.Select(x => x.Type == DocType.Document ? x.AsDocument : throw new FormatException("insertMany expects an array of documents.")));
                    PrintValue(new Document { ["insertedCount"] = ids.Count, ["insertedIds"] = new DocArray(ids) });
                }
                else if (call.Name == "insertMany") throw new FormatException("insertMany expects an array of documents.");
                else PrintValue(new Document { ["insertedId"] = col.Insert(RequiredDoc(call, 0)) });
                break;
            }
            case "update" or "updateOne" or "updateMany" or "replaceOne":
            {
                var filter = RequiredDoc(call, 0);
                var update = RequiredDoc(call, 1);
                bool upsert = Option(call, 2, "upsert");
                bool many = call.Name == "updateMany" || (call.Name == "update" && Option(call, 2, "multi"));
                var r = call.Name == "replaceOne" ? col.ReplaceOne(filter, update, upsert)
                    : many ? col.UpdateMany(filter, update, upsert) : col.UpdateOne(filter, update, upsert);
                var res = new Document { ["matchedCount"] = r.MatchedCount, ["modifiedCount"] = r.ModifiedCount };
                if (r.UpsertedId is { } id) res["upsertedId"] = id;
                PrintValue(res);
                break;
            }
            case "delete" or "deleteOne" or "remove" or "deleteMany":
            {
                var filter = DocArg(call, 0) ?? (call.Name is "deleteMany" or "remove" ? new Document() : throw new FormatException($"{call.Name} requires a filter."));
                bool one = call.Name is "deleteOne" or "delete" || (call.Name == "remove" && Option(call, 1, "justOne"));
                PrintValue(new Document { ["deletedCount"] = one ? col.DeleteOne(filter) : col.DeleteMany(filter) });
                break;
            }
            case "createIndex" or "ensureIndex":
            {
                var keys = RequiredDoc(call, 0);
                if (keys.Count != 1) throw new FormatException("Only single-field indexes are supported: createIndex({ field: 1 }).");
                _out.WriteLine(col.CreateIndex(keys.Keys.First(), Option(call, 1, "unique")));
                break;
            }
            case "dropIndex":
            {
                var v = Arg(call, 0);
                string name = v.Type == DocType.Document ? v.AsDocument.Keys.First() : v.AsString;
                _out.WriteLine(col.DropIndex(name));
                break;
            }
            case "getIndexes": ListIndexes(col); break;
            case "drop": _out.WriteLine(col.Drop()); break;
            default: throw new FormatException($"Unknown collection method '{call.Name}'.");
        }
    }

    private void PrintValue(DocValue v) => _out.WriteLine(DocJson.WriteValue(v, _pretty));

    // ------------------------------------------------------------------ dot commands

    private void DotCommand(string text)
    {
        var parts = SplitArgs(text);
        switch (parts[0])
        {
            case ".help": _out.WriteLine(Help); break;
            case ".quit" or ".exit": QuitRequested = true; break;
            case ".collections" or ".tables": ListCollections(); break;
            case ".indexes" or ".indices":
                foreach (var name in parts.Count > 1 ? [parts[1]] : _db.GetCollectionNames())
                {
                    if (parts.Count == 1) _out.WriteLine(name + ":");
                    ListIndexes(Col(name));
                }
                break;
            case ".stats": Stats(); break;
            case ".checkpoint": Checkpoint(); break;
            case ".integrity" or ".check":
                _db.CheckIntegrity();
                _out.WriteLine("ok");
                break;
            case ".timer":
                _timer = parts.Count > 1 ? parts[1] == "on" : !_timer;
                break;
            case ".mode":
                if (parts.Count < 2 || parts[1] is not ("json" or "pretty")) throw new FormatException("Usage: .mode json|pretty");
                _pretty = parts[1] == "pretty";
                break;
            case ".dump": Dump(parts.Count > 1 ? parts[1] : null, _out); break;
            case ".export":
                if (parts.Count < 3) throw new FormatException("Usage: .export <collection> <file.jsonl>");
                Export(parts[1], parts[2]);
                break;
            case ".import":
                if (parts.Count < 3) throw new FormatException("Usage: .import <file.jsonl|file.json> <collection>");
                Import(parts[1], parts[2]);
                break;
            case ".read":
                if (parts.Count < 2) throw new FormatException("Usage: .read <script-file>");
                using (var r = new StreamReader(parts[1]))
                {
                    int errors = RunReader(r, interactive: false, bail: true);
                    if (errors > 0) throw new FolioException($"{parts[1]}: {errors} statement(s) failed.");
                }
                break;
            default: throw new FormatException($"Unknown command '{parts[0]}' (try .help).");
        }
    }

    private static List<string> SplitArgs(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (char c in text)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else sb.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) list.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private void ListCollections()
    {
        foreach (var n in _db.GetCollectionNames()) _out.WriteLine(n);
    }

    private void ListIndexes(Collection col)
    {
        foreach (var i in col.GetIndexes())
            _out.WriteLine($"  {i.Name,-24} {i.Field}{(i.Unique ? " unique" : "")}{(i.MultiKey ? " multikey" : "")}");
    }

    private void Stats()
    {
        var s = _db.GetStats();
        var doc = new Document
        {
            ["path"] = _db.Path,
            ["pageSize"] = s.PageSize,
            ["pageCount"] = s.PageCount,
            ["freePages"] = s.FreePages,
            ["walFrames"] = s.WalFrames,
            ["fileBytes"] = s.DatabaseFileBytes,
            ["walBytes"] = s.WalFileBytes,
            ["collections"] = new DocArray(s.Collections.Select(c => (DocValue)new Document { ["name"] = c, ["count"] = Col(c).Count() })),
        };
        _out.WriteLine(DocJson.Write(doc, indented: true));
    }

    private void Checkpoint()
    {
        if (_tx is not null) throw new InvalidOperationException("Commit or roll back the open transaction first.");
        _out.WriteLine(_db.Checkpoint() ? "ok" : "busy (readers active)");
    }

    /// <summary>Writes a script that recreates the collections (indexes first, then documents).</summary>
    private void Dump(string? only, TextWriter w)
    {
        using var snap = _db.BeginSnapshot();
        w.WriteLine("// FolioDb dump");
        w.WriteLine("begin");
        foreach (var name in snap.GetCollectionNames())
        {
            if (only is not null && name != only) continue;
            var col = snap.GetCollection(name);
            string target = IsIdentifier(name) ? "db." + name : $"db.getCollection({DocJson.WriteValue(name)})";
            foreach (var i in col.GetIndexes().Where(i => i.Field != "_id"))
                w.WriteLine($"{target}.createIndex({{ {DocJson.WriteValue(i.Field)}: 1 }}{(i.Unique ? ", { unique: true }" : "")})");
            foreach (var d in col.Find()) w.WriteLine($"{target}.insert({DocJson.Write(d)})");
        }
        w.WriteLine("commit");
    }

    private static bool IsIdentifier(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    private void Export(string collection, string file)
    {
        using var snap = _db.BeginSnapshot();
        using var w = new StreamWriter(file, append: false, new UTF8Encoding(false));
        long n = 0;
        foreach (var d in snap.GetCollection(collection).Find())
        {
            w.WriteLine(DocJson.Write(d));
            n++;
        }
        _out.WriteLine($"Exported {n} document(s) to {file}.");
    }

    private void Import(string file, string collection)
    {
        var text = File.ReadAllText(file);
        IEnumerable<Document> docs;
        if (text.TrimStart().StartsWith('['))
            docs = DocJson.Parse(text).AsArray.Select(v => v.AsDocument);
        else
            docs = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Select(Document.Parse);
        var ids = Col(collection).InsertMany(docs.ToList());
        _out.WriteLine($"Imported {ids.Count} document(s) into {collection}.");
    }

    private const string Help = """
        Statements (JSON arguments accept shell syntax: unquoted keys, 'single quotes', ObjectId(..), ISODate(..)):
          db.<col>.insert({...}) | insertMany([{...}, ...])
          db.<col>.find({filter}, {projection}).sort({f: 1}).skip(n).limit(n) | .count() | .explain()
          db.<col>.findOne({filter}) | count({filter}) | explain({filter})
          db.<col>.updateOne({filter}, {$set: {...}}, {upsert: true}) | updateMany(...) | replaceOne(...)
          db.<col>.deleteOne({filter}) | deleteMany({filter})
          db.<col>.createIndex({field: 1}, {unique: true}) | dropIndex('field') | getIndexes() | drop()
          db.getCollection('name').find() | db.getCollectionNames() | db.stats()
          begin | commit | rollback          explicit transaction
        Dot commands:
          .collections              list collections
          .indexes [col]            list indexes
          .dump [col]               print a script that recreates the data
          .export <col> <file>      write documents as JSON lines
          .import <file> <col>      load a JSON-lines or JSON-array file
          .read <file>              run statements from a file
          .stats | .checkpoint | .integrity
          .mode json|pretty         output format
          .timer on|off             show execution time
          .quit
        """;
}

internal sealed record Call(string Name, List<DocValue>? Args);

/// <summary>Parses <c>ident(.ident(args))*</c> chains, e.g. <c>db.users.find({...}).limit(3)</c>.</summary>
internal static class CallParser
{
    public static List<Call> Parse(string text)
    {
        var calls = new List<Call>();
        int i = 0;
        while (true)
        {
            SkipWs(text, ref i);
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;
            if (i == start) throw new FormatException($"Expected an identifier at position {i}.");
            string name = text[start..i];
            SkipWs(text, ref i);
            List<DocValue>? args = null;
            if (i < text.Length && text[i] == '(')
            {
                int close = FindClose(text, i);
                args = DocJson.ParseList(text[(i + 1)..close]);
                i = close + 1;
                SkipWs(text, ref i);
            }
            calls.Add(new Call(name, args));
            if (i >= text.Length) return calls;
            if (text[i] != '.') throw new FormatException($"Unexpected '{text[i]}' at position {i}.");
            i++;
        }
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    private static int FindClose(string s, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']')
            {
                depth--;
                if (depth == 0)
                {
                    if (c != ')') throw new FormatException($"Mismatched '{c}' at position {i}.");
                    return i;
                }
            }
        }
        throw new FormatException("Unbalanced parentheses.");
    }
}
