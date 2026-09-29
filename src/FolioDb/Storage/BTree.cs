using System.Buffers.Binary;

namespace FolioDb.Storage;

/// <summary>
/// B+Tree over <see cref="StorageTx"/> pages with variable-length byte keys (memcmp order) and values.
/// <para>Page layout (slotted page):</para>
/// <code>
/// [0] type (1 = leaf, 2 = interior)   [2..4) cell count   [4..6) content start   [6..8) fragmented bytes
/// [8..12) right-most child (interior)  [12..) ushort slot offsets, sorted by key; cell content grows down from the end.
/// leaf cell:     u16 keyLen, u8 flags, u32 valueLen, key, (value | u32 firstOverflowPage)
/// interior cell: u16 keyLen, u32 child, key          (child holds keys &lt; key; right-most child holds the rest)
/// overflow page: u32 nextPage, data...
/// </code>
/// The root page number never changes (root splits move the old root's content into a new child).
/// </summary>
internal readonly struct BTree
{
    public const byte LeafType = 1;
    public const byte InteriorType = 2;
    private const int HeaderSize = 12;
    private const int LeafCellHeader = 7;
    private const int InteriorCellHeader = 6;
    private const byte FlagOverflow = 1;

    private readonly StorageTx _tx;
    public readonly uint Root;

    public BTree(StorageTx tx, uint root)
    {
        _tx = tx;
        Root = root;
    }

    private int PageSize => _tx.PageSize;

    /// <summary>Largest key accepted (keeps at least four cells per page so splits always succeed).</summary>
    public static int MaxKeySize(int pageSize) => (pageSize - HeaderSize) / 4 - 16;
    private int MaxInlineCell => (PageSize - HeaderSize) / 4 - 2;

    public static uint Create(StorageTx tx)
    {
        uint pg = tx.AllocatePage();
        InitEmptyLeaf(tx.WritePage(pg), tx.PageSize);
        return pg;
    }

    public static void InitEmptyLeaf(Span<byte> page, int pageSize)
    {
        page[..HeaderSize].Clear();
        page[0] = LeafType;
        SetContentStart(page, pageSize, pageSize);
    }

    // ------------------------------------------------------------------ page accessors

    private static int Count(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
    private static void SetCount(Span<byte> p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p[2..], (ushort)v);
    private static int ContentStart(ReadOnlySpan<byte> p, int pageSize)
    {
        int v = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
        return v == 0 ? pageSize : v;
    }
    private static void SetContentStart(Span<byte> p, int v, int pageSize) =>
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], (ushort)(v == pageSize ? 0 : v));
    private static int Frag(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
    private static void SetFrag(Span<byte> p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p[6..], (ushort)v);
    private static uint RightChild(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
    private static void SetRightChild(Span<byte> p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(p[8..], v);
    private static int Slot(ReadOnlySpan<byte> p, int i) => BinaryPrimitives.ReadUInt16LittleEndian(p[(HeaderSize + 2 * i)..]);
    private static void SetSlot(Span<byte> p, int i, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p[(HeaderSize + 2 * i)..], (ushort)v);
    private static bool IsLeaf(ReadOnlySpan<byte> p) => p[0] == LeafType;

    private static ReadOnlySpan<byte> KeyAt(ReadOnlySpan<byte> p, int i)
    {
        int off = Slot(p, i);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(p[off..]);
        return p.Slice(off + (IsLeaf(p) ? LeafCellHeader : InteriorCellHeader), keyLen);
    }

    private static uint ChildAt(ReadOnlySpan<byte> p, int i) =>
        i == Count(p) ? RightChild(p) : BinaryPrimitives.ReadUInt32LittleEndian(p[(Slot(p, i) + 2)..]);

    private static void SetChildAt(Span<byte> p, int i, uint child)
    {
        if (i == Count(p)) SetRightChild(p, child);
        else BinaryPrimitives.WriteUInt32LittleEndian(p[(Slot(p, i) + 2)..], child);
    }

    private static int CellSize(ReadOnlySpan<byte> p, int off)
    {
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(p[off..]);
        if (!IsLeaf(p)) return InteriorCellHeader + keyLen;
        bool ovf = (p[off + 2] & FlagOverflow) != 0;
        int valLen = BinaryPrimitives.ReadInt32LittleEndian(p[(off + 3)..]);
        return LeafCellHeader + keyLen + (ovf ? 4 : valLen);
    }

    private static ReadOnlySpan<byte> CellBytes(ReadOnlySpan<byte> p, int i)
    {
        int off = Slot(p, i);
        return p.Slice(off, CellSize(p, off));
    }

    /// <summary>First index whose key is &gt;= <paramref name="key"/>.</summary>
    private static int LowerBound(ReadOnlySpan<byte> p, ReadOnlySpan<byte> key, out bool found)
    {
        int lo = 0, hi = Count(p) - 1;
        found = false;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = KeyAt(p, mid).SequenceCompareTo(key);
            if (c < 0) lo = mid + 1;
            else
            {
                if (c == 0) found = true;
                hi = mid - 1;
            }
        }
        return lo;
    }

    /// <summary>Child index to descend into for <paramref name="key"/> (first cell whose key is &gt; key, else right-most).</summary>
    private static int ChildIndex(ReadOnlySpan<byte> p, ReadOnlySpan<byte> key)
    {
        int lo = 0, hi = Count(p) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (KeyAt(p, mid).SequenceCompareTo(key) <= 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return lo;
    }

    // ------------------------------------------------------------------ cell insert / remove

    private bool TryInsertCell(byte[] page, int index, ReadOnlySpan<byte> cell)
    {
        int count = Count(page);
        int need = cell.Length + 2;
        int free = ContentStart(page, PageSize) - (HeaderSize + 2 * count);
        if (free < need)
        {
            if (free + Frag(page) < need) return false;
            Defragment(page);
        }
        int start = ContentStart(page, PageSize) - cell.Length;
        cell.CopyTo(page.AsSpan(start));
        SetContentStart(page, start, PageSize);
        var slots = page.AsSpan(HeaderSize);
        slots.Slice(2 * index, 2 * (count - index)).CopyTo(slots[(2 * index + 2)..]);
        SetSlot(page, index, start);
        SetCount(page, count + 1);
        return true;
    }

    private void RemoveCell(byte[] page, int index)
    {
        int count = Count(page);
        int off = Slot(page, index);
        int size = CellSize(page, off);
        var slots = page.AsSpan(HeaderSize);
        slots.Slice(2 * (index + 1), 2 * (count - index - 1)).CopyTo(slots[(2 * index)..]);
        count--;
        SetCount(page, count);
        if (count == 0)
        {
            SetContentStart(page, PageSize, PageSize);
            SetFrag(page, 0);
        }
        else if (off == ContentStart(page, PageSize)) SetContentStart(page, off + size, PageSize);
        else SetFrag(page, Frag(page) + size);
    }

    private void Defragment(byte[] page)
    {
        var cells = ReadCells(page);
        Build(page, page[0], cells, RightChild(page));
    }

    private static List<byte[]> ReadCells(ReadOnlySpan<byte> page)
    {
        int n = Count(page);
        var cells = new List<byte[]>(n + 1);
        for (int i = 0; i < n; i++) cells.Add(CellBytes(page, i).ToArray());
        return cells;
    }

    private void Build(byte[] page, byte type, List<byte[]> cells, uint rightChild)
    {
        Array.Clear(page);
        page[0] = type;
        SetRightChild(page, rightChild);
        int pos = PageSize;
        for (int i = 0; i < cells.Count; i++)
        {
            pos -= cells[i].Length;
            cells[i].CopyTo(page, pos);
            SetSlot(page, i, pos);
        }
        if (HeaderSize + 2 * cells.Count > pos) throw new InvalidOperationException("B+Tree page overflow while building page.");
        SetCount(page, cells.Count);
        SetContentStart(page, pos, PageSize);
    }

    private static ReadOnlySpan<byte> CellKey(ReadOnlySpan<byte> cell, bool leaf)
    {
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(cell);
        return cell.Slice(leaf ? LeafCellHeader : InteriorCellHeader, keyLen);
    }

    private static uint CellChild(ReadOnlySpan<byte> cell) => BinaryPrimitives.ReadUInt32LittleEndian(cell[2..]);

    private static byte[] InteriorCell(ReadOnlySpan<byte> key, uint child)
    {
        var cell = new byte[InteriorCellHeader + key.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(cell, (ushort)key.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(cell.AsSpan(2), child);
        key.CopyTo(cell.AsSpan(InteriorCellHeader));
        return cell;
    }

    private byte[] LeafCell(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        bool overflow = LeafCellHeader + key.Length + value.Length > MaxInlineCell;
        var cell = new byte[LeafCellHeader + key.Length + (overflow ? 4 : value.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(cell, (ushort)key.Length);
        cell[2] = overflow ? FlagOverflow : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(cell.AsSpan(3), value.Length);
        key.CopyTo(cell.AsSpan(LeafCellHeader));
        if (overflow) BinaryPrimitives.WriteUInt32LittleEndian(cell.AsSpan(LeafCellHeader + key.Length), WriteOverflow(value));
        else value.CopyTo(cell.AsSpan(LeafCellHeader + key.Length));
        return cell;
    }

    // ------------------------------------------------------------------ overflow chains

    private uint WriteOverflow(ReadOnlySpan<byte> value)
    {
        int chunk = PageSize - 4;
        uint first = 0;
        byte[]? prev = null;
        for (int pos = 0; pos < value.Length; pos += chunk)
        {
            uint pg = _tx.AllocatePage();
            var page = _tx.WritePage(pg);
            value.Slice(pos, Math.Min(chunk, value.Length - pos)).CopyTo(page.AsSpan(4));
            if (prev is null) first = pg;
            else BinaryPrimitives.WriteUInt32LittleEndian(prev, pg);
            prev = page;
        }
        return first;
    }

    private void FreeOverflow(uint first)
    {
        while (first != 0)
        {
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(_tx.ReadPage(first));
            _tx.FreePage(first);
            first = next;
        }
    }

    private static ReadOnlySpan<byte> ReadLeafValue(StorageTx tx, ReadOnlySpan<byte> page, int index)
    {
        int off = Slot(page, index);
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(page[off..]);
        int valLen = BinaryPrimitives.ReadInt32LittleEndian(page[(off + 3)..]);
        int vpos = off + LeafCellHeader + keyLen;
        if ((page[off + 2] & FlagOverflow) == 0) return page.Slice(vpos, valLen);

        var result = new byte[valLen];
        uint pg = BinaryPrimitives.ReadUInt32LittleEndian(page[vpos..]);
        int chunk = tx.PageSize - 4;
        for (int pos = 0; pos < valLen; pos += chunk)
        {
            var op = tx.ReadPage(pg);
            op.AsSpan(4, Math.Min(chunk, valLen - pos)).CopyTo(result.AsSpan(pos));
            pg = BinaryPrimitives.ReadUInt32LittleEndian(op);
        }
        return result;
    }

    private void FreeCellOverflow(ReadOnlySpan<byte> page, int index)
    {
        int off = Slot(page, index);
        if ((page[off + 2] & FlagOverflow) == 0) return;
        int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(page[off..]);
        FreeOverflow(BinaryPrimitives.ReadUInt32LittleEndian(page[(off + LeafCellHeader + keyLen)..]));
    }

    // ------------------------------------------------------------------ public operations

    public bool TryGet(ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        uint pg = Root;
        while (true)
        {
            var page = _tx.ReadPage(pg);
            if (IsLeaf(page))
            {
                int i = LowerBound(page, key, out bool found);
                value = found ? ReadLeafValue(_tx, page, i) : default;
                return found;
            }
            pg = ChildAt(page, ChildIndex(page, key));
        }
    }

    /// <summary>Key lookup that never reads the value (no overflow chain traversal).</summary>
    public bool ContainsKey(ReadOnlySpan<byte> key)
    {
        uint pg = Root;
        while (true)
        {
            var page = _tx.ReadPage(pg);
            if (IsLeaf(page))
            {
                LowerBound(page, key, out bool found);
                return found;
            }
            pg = ChildAt(page, ChildIndex(page, key));
        }
    }

    /// <summary>
    /// Replaces the value of <paramref name="key"/>. A value of the same size as the stored one is overwritten in place,
    /// dirtying only the leaf or the overflow pages whose bytes actually change; otherwise falls back to <see cref="Insert"/>.
    /// </summary>
    public void Update(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        uint pg = Root;
        byte[] page;
        while (true)
        {
            page = _tx.ReadPage(pg);
            if (IsLeaf(page)) break;
            pg = ChildAt(page, ChildIndex(page, key));
        }
        int idx = LowerBound(page, key, out bool found);
        int off = found ? Slot(page, idx) : 0;
        if (!found || BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(off + 3)) != value.Length)
        {
            Insert(key, value, overwrite: true);
            return;
        }

        int vpos = off + LeafCellHeader + BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(off));
        if ((page[off + 2] & FlagOverflow) == 0)
        {
            if (!page.AsSpan(vpos, value.Length).SequenceEqual(value)) value.CopyTo(_tx.WritePage(pg).AsSpan(vpos));
            return;
        }

        uint opg = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(vpos));
        int chunk = PageSize - 4;
        for (int pos = 0; pos < value.Length; pos += chunk)
        {
            var op = _tx.ReadPage(opg);
            var part = value.Slice(pos, Math.Min(chunk, value.Length - pos));
            if (!op.AsSpan(4, part.Length).SequenceEqual(part)) part.CopyTo(_tx.WritePage(opg).AsSpan(4));
            opg = BinaryPrimitives.ReadUInt32LittleEndian(op);
        }
    }

    /// <summary>Inserts or replaces. Returns false (and does nothing) if the key exists and <paramref name="overwrite"/> is false.</summary>
    public bool Insert(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool overwrite)
    {
        if (key.Length > MaxKeySize(PageSize))
            throw new FolioException($"Key of {key.Length} bytes exceeds the maximum of {MaxKeySize(PageSize)} bytes for page size {PageSize}.");

        var path = new List<(uint Page, int Index)>(8);
        uint pg = Root;
        while (true)
        {
            var page = _tx.ReadPage(pg);
            if (IsLeaf(page)) break;
            int ci = ChildIndex(page, key);
            path.Add((pg, ci));
            pg = ChildAt(page, ci);
        }

        var leaf = _tx.ReadPage(pg);
        int idx = LowerBound(leaf, key, out bool exists);
        if (exists && !overwrite) return false;

        var writable = _tx.WritePage(pg);
        if (exists)
        {
            FreeCellOverflow(writable, idx);
            RemoveCell(writable, idx);
        }
        var cell = LeafCell(key, value);
        if (!TryInsertCell(writable, idx, cell)) SplitAndInsert(path, pg, idx, cell);
        return true;
    }

    private void SplitAndInsert(List<(uint Page, int Index)> path, uint pg, int index, byte[] cell)
    {
        var page = _tx.WritePage(pg);
        bool leaf = IsLeaf(page);
        uint oldRight = RightChild(page);
        var cells = ReadCells(page);
        cells.Insert(index, cell);

        if (pg == Root)
        {
            // Keep the root page number stable: the root becomes an interior node pointing at a new child
            // that takes over the root's (overfull) content, and the child is split below.
            uint child = _tx.AllocatePage();
            Build(page, InteriorType, new List<byte[]>(), child);
            path.Add((Root, 0));
            pg = child;
            page = _tx.WritePage(child);
        }

        int total = 0;
        foreach (var c in cells) total += c.Length + 2;

        uint newPg = _tx.AllocatePage();
        var newPage = _tx.WritePage(newPg);
        byte[] separator;
        if (leaf)
        {
            int k = SplitPoint(cells, total, 1, cells.Count - 1);
            separator = CellKey(cells[k], leaf: true).ToArray();
            Build(page, LeafType, cells.GetRange(0, k), 0);
            Build(newPage, LeafType, cells.GetRange(k, cells.Count - k), 0);
        }
        else
        {
            int k = SplitPoint(cells, total, 1, cells.Count - 2);
            separator = CellKey(cells[k], leaf: false).ToArray();
            Build(page, InteriorType, cells.GetRange(0, k), CellChild(cells[k]));
            Build(newPage, InteriorType, cells.GetRange(k + 1, cells.Count - k - 1), oldRight);
        }

        // Parent: pg now holds keys < separator; newPg holds keys >= separator and replaces pg's old slot.
        var (parentPg, parentIdx) = path[^1];
        path.RemoveAt(path.Count - 1);
        var parent = _tx.WritePage(parentPg);
        SetChildAt(parent, parentIdx, newPg);
        var sepCell = InteriorCell(separator, pg);
        if (!TryInsertCell(parent, parentIdx, sepCell)) SplitAndInsert(path, parentPg, parentIdx, sepCell);
    }

    private static int SplitPoint(List<byte[]> cells, int total, int min, int max)
    {
        int acc = 0, k = 0;
        while (k < cells.Count && acc < total / 2) acc += cells[k++].Length + 2;
        return Math.Clamp(k, min, max);
    }

    public bool Delete(ReadOnlySpan<byte> key)
    {
        var path = new List<(uint Page, int Index)>(8);
        uint pg = Root;
        while (true)
        {
            var page = _tx.ReadPage(pg);
            if (IsLeaf(page)) break;
            int ci = ChildIndex(page, key);
            path.Add((pg, ci));
            pg = ChildAt(page, ci);
        }

        var leaf = _tx.ReadPage(pg);
        int idx = LowerBound(leaf, key, out bool found);
        if (!found) return false;

        var writable = _tx.WritePage(pg);
        FreeCellOverflow(writable, idx);
        RemoveCell(writable, idx);
        if (Count(writable) == 0 && pg != Root) RemoveEmptyChild(path, pg);
        CollapseRoot();
        return true;
    }

    private void RemoveEmptyChild(List<(uint Page, int Index)> path, uint child)
    {
        _tx.FreePage(child);
        var (ppg, pidx) = path[^1];
        path.RemoveAt(path.Count - 1);
        var parent = _tx.WritePage(ppg);
        int n = Count(parent);
        if (pidx < n)
        {
            RemoveCell(parent, pidx); // keys of the removed range now fall into the next child
        }
        else if (n > 0)
        {
            uint newRight = ChildAt(parent, n - 1);
            RemoveCell(parent, n - 1);
            SetRightChild(parent, newRight);
        }
        else if (ppg == Root)
        {
            InitEmptyLeaf(parent, PageSize);
        }
        else
        {
            RemoveEmptyChild(path, ppg);
        }
    }

    private void CollapseRoot()
    {
        while (true)
        {
            var root = _tx.ReadPage(Root);
            if (IsLeaf(root) || Count(root) > 0) return;
            uint child = RightChild(root);
            var childPage = _tx.ReadPage(child);
            childPage.CopyTo(_tx.WritePage(Root), 0);
            _tx.FreePage(child);
        }
    }

    /// <summary>Frees every page of the tree (including overflow chains and the root).</summary>
    public void Drop() => FreeSubtree(Root);

    private void FreeSubtree(uint pg)
    {
        var page = _tx.ReadPage(pg);
        int n = Count(page);
        if (IsLeaf(page))
        {
            for (int i = 0; i < n; i++) FreeCellOverflow(page, i);
        }
        else
        {
            for (int i = 0; i <= n; i++) FreeSubtree(ChildAt(page, i));
        }
        _tx.FreePage(pg);
    }

    /// <summary>Removes all entries but keeps the (now empty) root.</summary>
    public void Clear()
    {
        var page = _tx.ReadPage(Root);
        int n = Count(page);
        if (IsLeaf(page))
        {
            for (int i = 0; i < n; i++) FreeCellOverflow(page, i);
        }
        else
        {
            for (int i = 0; i <= n; i++) FreeSubtree(ChildAt(page, i));
        }
        InitEmptyLeaf(_tx.WritePage(Root), PageSize);
    }

    public Cursor CreateCursor() => new(_tx, Root);

    /// <summary>Forward cursor. The tree must not be modified while a cursor is in use.</summary>
    public sealed class Cursor
    {
        private readonly StorageTx _tx;
        private readonly uint _root;
        private readonly List<(uint Page, byte[] Data, int Index)> _stack = new(8);
        private byte[] _leaf = [];
        private int _index;

        public Cursor(StorageTx tx, uint root)
        {
            _tx = tx;
            _root = root;
        }

        public bool IsValid { get; private set; }
        public ReadOnlySpan<byte> Key => KeyAt(_leaf, _index);
        public ReadOnlySpan<byte> Value => ReadLeafValue(_tx, _leaf, _index);

        public bool SeekFirst()
        {
            _stack.Clear();
            DescendLeftmost(_root);
            _index = 0;
            return Settle();
        }

        /// <summary>Positions at the first key &gt;= <paramref name="key"/>.</summary>
        public bool Seek(ReadOnlySpan<byte> key)
        {
            _stack.Clear();
            uint pg = _root;
            while (true)
            {
                var page = _tx.ReadPage(pg);
                if (IsLeaf(page))
                {
                    _leaf = page;
                    _index = LowerBound(page, key, out _);
                    return Settle();
                }
                int ci = ChildIndex(page, key);
                _stack.Add((pg, page, ci));
                pg = ChildAt(page, ci);
            }
        }

        /// <summary>
        /// Positions exactly at <paramref name="key"/> if present. Consecutive lookups reuse the current leaf and the
        /// cached interior path, so ascending (or nearby) keys avoid re-reading pages from the root.
        /// </summary>
        public bool SeekExact(ReadOnlySpan<byte> key)
        {
            bool found;
            int n = _leaf.Length == 0 ? 0 : Count(_leaf);
            if (n > 0 && KeyAt(_leaf, 0).SequenceCompareTo(key) <= 0 && KeyAt(_leaf, n - 1).SequenceCompareTo(key) >= 0)
            {
                _index = LowerBound(_leaf, key, out found);
                IsValid = found;
                return found;
            }
            uint pg = _root;
            for (int level = 0; ; level++)
            {
                byte[] page;
                if (level < _stack.Count && _stack[level].Page == pg) page = _stack[level].Data;
                else
                {
                    if (level < _stack.Count) _stack.RemoveRange(level, _stack.Count - level);
                    page = _tx.ReadPage(pg);
                }
                if (IsLeaf(page))
                {
                    if (level < _stack.Count) _stack.RemoveRange(level, _stack.Count - level);
                    _leaf = page;
                    _index = LowerBound(page, key, out found);
                    IsValid = found;
                    return found;
                }
                int ci = ChildIndex(page, key);
                if (level < _stack.Count) _stack[level] = (pg, page, ci);
                else _stack.Add((pg, page, ci));
                pg = ChildAt(page, ci);
            }
        }

        public bool MoveNext()
        {
            if (!IsValid) return false;
            _index++;
            return Settle();
        }

        private void DescendLeftmost(uint pg)
        {
            while (true)
            {
                var page = _tx.ReadPage(pg);
                if (IsLeaf(page))
                {
                    _leaf = page;
                    return;
                }
                _stack.Add((pg, page, 0));
                pg = ChildAt(page, 0);
            }
        }

        private bool Settle()
        {
            while (_index >= Count(_leaf))
            {
                // climb until a parent has another child to the right
                while (true)
                {
                    if (_stack.Count == 0)
                    {
                        IsValid = false;
                        return false;
                    }
                    var (pg, parent, idx) = _stack[^1];
                    if (idx < Count(parent))
                    {
                        _stack[^1] = (pg, parent, idx + 1);
                        DescendLeftmost(ChildAt(parent, idx + 1));
                        _index = 0;
                        break;
                    }
                    _stack.RemoveAt(_stack.Count - 1);
                }
            }
            IsValid = true;
            return true;
        }
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>Verifies ordering, separator bounds and uniform depth. Returns the number of entries.</summary>
    public long Verify()
    {
        int leafDepth = -1;
        return VerifyNode(Root, null, null, 0, ref leafDepth);
    }

    private long VerifyNode(uint pg, byte[]? lower, byte[]? upper, int depth, ref int leafDepth)
    {
        var page = _tx.ReadPage(pg);
        if (page[0] != LeafType && page[0] != InteriorType) throw new CorruptDatabaseException($"Page {pg} is not a B+Tree page.");
        int n = Count(page);
        for (int i = 0; i < n; i++)
        {
            var k = KeyAt(page, i);
            if (i > 0 && KeyAt(page, i - 1).SequenceCompareTo(k) >= 0) throw new CorruptDatabaseException($"Keys out of order in page {pg}.");
            if (lower is not null && k.SequenceCompareTo(lower) < 0) throw new CorruptDatabaseException($"Key below lower bound in page {pg}.");
            if (upper is not null && k.SequenceCompareTo(upper) >= 0) throw new CorruptDatabaseException($"Key above upper bound in page {pg}.");
        }
        if (IsLeaf(page))
        {
            if (leafDepth < 0) leafDepth = depth;
            else if (leafDepth != depth) throw new CorruptDatabaseException("Leaves are at different depths.");
            return n;
        }
        long total = 0;
        for (int i = 0; i <= n; i++)
        {
            byte[]? lo = i == 0 ? lower : KeyAt(page, i - 1).ToArray();
            byte[]? hi = i == n ? upper : KeyAt(page, i).ToArray();
            total += VerifyNode(ChildAt(page, i), lo, hi, depth + 1, ref leafDepth);
        }
        return total;
    }
}
