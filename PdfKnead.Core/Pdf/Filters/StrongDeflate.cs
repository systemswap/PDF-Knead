using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PdfKnead.Core.Pdf.Filters;

/// <summary>
/// Stronger Flate compression (RFC 1950/1951) for "Flate – strong (slower)" in the compressor. Adapted from libdeflate at level 12
/// (Copyright 2016 Eric Biggers, Copyright 2024 Google LLC; MIT licence, see THIRD-PARTY-NOTICES.md): block splitting, cost
/// adjustment, minimum match length, initial costs, parameters and the binary-tree match finder follow libdeflate, translated to C#
/// and extended (own hash for three-byte matches, early stop, second thread, match cache, package-merge): repeats are found with binary trees
/// (for every position all matches of increasing length); block boundaries come from statistics of coarse literal and match types that
/// change clearly (5 000 … 300 000 bytes; on a change, the block ends at the last check point); per block optimal parsing, i.e. the shortest
/// path by cost in 1/16 bit, up to ten passes with the code lengths of the previous pass, the best one wins; in addition the candidates
/// "literals only" and a static block with its own parsing; finally the smallest block type (dynamic tables, static, stored).
/// Every reader reads the result like ordinary Flate.
/// </summary>
/// <remarks>Thread-safe (the match finder is kept per thread); inputs of more than 300 000 bytes use a second thread for optimising and writing.</remarks>
public static class StrongDeflate
{
    private const int Window = 32768, MinMatch = 3, MaxMatch = 258;

    /// <summary>At most this many nodes per search in the tree.</summary>
    private const int MaxDepth = 300;

    /// <summary>From this length on, the covered positions are only inserted, not searched (long repeats, filled areas).</summary>
    private const int NiceLength = MaxMatch;

    /// <summary>Maximum number of parsing passes (it stops earlier when a pass gains nothing).</summary>
    private const int MaxPasses = 10;
    /// <summary>Further passes only while one gains at least this much (0.05 % of the block): same output size, an A0 plan 15.6 → 10.9 s.</summary>
    private const long MinGainPerTenThousand = 5;

    /// <summary>Block length: minimum (otherwise the header costs too much) and approximate maximum (then the block ends in any case).</summary>
    private const int MinBlock = 5000, SoftMaxBlock = 300_000;

    /// <summary>After this many new observations, a check is made whether the data has changed.</summary>
    private const int ObservationsPerCheck = 512;

    /// <summary>Up to this length a static block with its own parsing is tried as well (small blocks: the header does not pay off).</summary>
    private const int StaticUpTo = 10_000;

    /// <summary>Costs are in 1/16 bit.</summary>
    private const int BitCost = 16;

    /// <summary>At most this many matches per position (increasing length; in practice rarely more than ten).</summary>
    private const int MaxMatchesPerPosition = 32;

    private const int Nil = int.MinValue / 2;

    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073,
        4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    /// <summary>Length symbol (0 … 28, without the 257 offset) per length 3 … 258.</summary>
    private static readonly byte[] LengthSymbol = BuildLengthSymbols();

    /// <summary>Distance symbol per distance 1 … 256, or per (distance − 1) / 128 above that.</summary>
    private static readonly byte[] DistSymbolSmall = new byte[257], DistSymbolLarge = new byte[256];

    static StrongDeflate()
    {
        for (var s = 0; s < 30; s++)
            for (var d = DistBase[s]; d < DistBase[s] + (1 << DistExtra[s]) && d <= Window; d++)
                if (d <= 256) DistSymbolSmall[d] = (byte)s;
                else DistSymbolLarge[(d - 1) >> 7] = (byte)s;
    }

    private static byte[] BuildLengthSymbols()
    {
        var table = new byte[MaxMatch + 1];
        for (var s = 0; s < 29; s++)
            for (var l = LengthBase[s]; l < LengthBase[s] + (1 << LengthExtra[s]) && l <= MaxMatch; l++) table[l] = (byte)s;
        table[MaxMatch] = 28;
        return table;
    }

    private static int DistSymbol(int dist) => dist <= 256 ? DistSymbolSmall[dist] : DistSymbolLarge[(dist - 1) >> 7];

    /// <summary>Compresses with a zlib wrapper (like <c>ZLibStream</c>, but smaller).</summary>
    /// <param name="data">The data to compress.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The zlib data.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> data, CancellationToken cancel = default)
    {
        var input = data.ToArray();
        var output = new BitWriter(input.Length / 3 + 64);
        output.Byte(0x78);
        output.Byte(0xDA);
        Deflate(input, output, cancel);
        output.Align();
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Flate.Adler32(input));   // the same sum the reader checks (No. 937)
        foreach (var b in adler) output.Byte(b);
        return output.ToArray();
    }

    // ------------------------------------------------------------ Procedure

    private static void Deflate(byte[] input, BitWriter output, CancellationToken cancel)
    {
        var n = input.Length;
        if (n == 0)
        {
            // An empty static block: only the end code
            output.Bits(1, 1);
            output.Bits(1, 2);
            output.Bits(0, 7);
            return;
        }
        var finder = MatchFinder.For(input);
        var capacity = Math.Min(n, SoftMaxBlock + MinBlock + MaxMatch) + 1;
        // Work arrays rented (ArrayPool): allocating them anew on every call allocated 8.4 GB instead of 1.7 GB across the test corpus
        var cache = new MatchCache(capacity);
        // More than one block: optimising and writing run on a second thread while this one already searches the matches of the next block
        using var pipeline = n > SoftMaxBlock ? new Pipeline(input, capacity, output, cancel) : null;
        using var optimizer = pipeline is null ? new Optimizer(capacity) : null;
        try
        {
            Collect(input, finder, ref cache, capacity, optimizer, pipeline, output, cancel);
            pipeline?.Finish();
        }
        finally
        {
            cache.Dispose();
        }
    }

    /// <summary>Optimises and writes on a second thread, in block order; at most two blocks wait.</summary>
    private sealed class Pipeline : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(MatchCache Cache, int Start, int End, int Change, bool Final)> _queue = new(2);
        private readonly CancellationTokenSource _stop;
        private readonly Task _worker;

        public Pipeline(byte[] input, int capacity, BitWriter output, CancellationToken cancel)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var token = _stop.Token;
            _worker = Task.Run(() =>
            {
                try
                {
                    using var optimizer = new Optimizer(capacity);
                    foreach (var block in _queue.GetConsumingEnumerable(token))
                        using (block.Cache)
                            optimizer.Run(input, block.Start, block.End, block.Cache, block.Change, block.Final, output);
                }
                catch
                {
                    // Otherwise the match finder would wait at the full queue
                    _stop.Cancel();
                    throw;
                }
            }, CancellationToken.None);
        }

        public void Add(MatchCache cache, int start, int end, int change, bool final)
        {
            try
            {
                _queue.Add((cache, start, end, change, final), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                cache.Dispose();
                Finish();   // rethrows the actual error of the optimiser
                throw;
            }
        }

        /// <summary>All blocks handed over: waits until the last one is written (errors of the optimiser surface here).</summary>
        public void Finish()
        {
            _queue.CompleteAdding();
            _worker.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (!_queue.IsAddingCompleted)
            {
                // Aborted while searching: stop the optimiser, write nothing more
                _stop.Cancel();
                _queue.CompleteAdding();
                try
                {
                    _worker.Wait();
                }
                catch (AggregateException)
                {
                }
            }
            while (_queue.TryTake(out var left)) left.Cache.Dispose();
            _queue.Dispose();
            _stop.Dispose();
        }
    }

    private static void Collect(byte[] input, MatchFinder finder, ref MatchCache cache, int capacity, Optimizer? optimizer, Pipeline? pipeline,
        BitWriter output, CancellationToken cancel)
    {
        var n = input.Length;
        var stats = new SplitStats();
        Span<ushort> lengths = stackalloc ushort[MaxMatchesPerPosition];
        Span<ushort> distances = stackalloc ushort[MaxMatchesPerPosition];
        var pos = 0;
        var blockStart = 0;
        var first = true;
        while (blockStart < n)
        {
            cancel.ThrowIfCancellationRequested();
            var maxEnd = n - blockStart < SoftMaxBlock + MinBlock ? n : blockStart + SoftMaxBlock;
            var minLength = ObservedMinLength(input, blockStart, maxEnd);
            var nextObservation = pos;
            var lastCheck = -1;
            var changed = false;
            while (pos < n)
            {
                var count = finder.Find(pos, true, lengths, distances);
                cache.Add(lengths[..count], distances[..count]);
                var best = count > 0 ? lengths[count - 1] : 0;
                if (pos >= nextObservation)
                {
                    if (best >= minLength)
                    {
                        stats.Match(best);
                        nextObservation = pos + best;
                    }
                    else
                    {
                        stats.Literal(input[pos]);
                        nextObservation = pos + 1;
                    }
                }
                pos++;
                // Long repeat: only insert the covered positions (filled areas, identical rows)
                if (best >= NiceLength)
                    for (var skip = best - 1; skip > 0 && pos < n; skip--, pos++)
                    {
                        finder.Find(pos, false, lengths, distances);
                        cache.Add([], []);
                    }
                if (pos >= maxEnd) break;
                if (!stats.ReadyToCheck(pos - blockStart, n - pos)) continue;
                if (stats.ShouldEnd(pos - blockStart))
                {
                    changed = true;
                    break;
                }
                stats.MergeNew();
                lastCheck = pos;
            }
            // On a change the block ends before the differing part; its matches and observations belong to the next block
            var blockEnd = changed && lastCheck >= 0 ? lastCheck : pos;
            var change = first ? 4 : stats.ChangeFromPrevious();
            if (pipeline is null)
            {
                optimizer!.Run(input, blockStart, blockEnd, cache, change, blockEnd == n, output);
                cache.DropFront(blockEnd - blockStart);
            }
            else
            {
                // The block goes to the optimiser with its cache; the part behind it that was already searched goes into a new cache
                var tail = cache.SplitAt(blockEnd - blockStart, capacity);
                var block = cache;
                cache = tail;
                pipeline.Add(block, blockStart, blockEnd, change, blockEnd == n);
            }
            first = false;
            stats.NextBlock(keepNew: blockEnd < pos);
            blockStart = blockEnd;
        }
    }

    /// <summary>
    /// Minimum match length for the statistics (not for parsing): few different literals are cheap, so short matches then
    /// do not count as matches.
    /// </summary>
    private static int ObservedMinLength(byte[] input, int start, int end)
    {
        var used = UsedLiterals(input.AsSpan(start, end - start));
        return used < 6 ? 9 : used < 8 ? 8 : used < 10 ? 7 : used < 16 ? 6 : used < 45 ? 5 : used < 80 ? 4 : 3;
    }

    /// <summary>Counts how many different byte values occur more than rarely (more often than once per 2 048 bytes).</summary>
    private static int UsedLiterals(ReadOnlySpan<byte> data)
    {
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        foreach (var b in data) histogram[b]++;
        var cutoff = data.Length >> 11;
        var used = 0;
        foreach (var h in histogram)
            if (h > cutoff) used++;
        return Math.Max(1, used);
    }

    // ------------------------------------------------------------ Block boundaries

    /// <summary>
    /// Coarse statistics of the data: eight literal types (the upper two bits and the lowest bit) and short/long matches. If the distribution of
    /// the new observations differs enough from the previous ones, the block ends; the longer it already is, the sooner.
    /// </summary>
    private sealed class SplitStats
    {
        private const int Types = 10;
        private readonly int[] _old = new int[Types], _new = new int[Types], _previous = new int[Types];
        private int _oldCount, _newCount, _previousCount;

        public void Literal(byte value)
        {
            _new[((value >> 5) & 6) | (value & 1)]++;
            _newCount++;
        }

        public void Match(int length)
        {
            _new[8 + (length >= 9 ? 1 : 0)]++;
            _newCount++;
        }

        public bool ReadyToCheck(int blockLength, int remaining) =>
            _newCount >= ObservationsPerCheck && blockLength >= MinBlock && remaining >= MinBlock;

        public bool ShouldEnd(int blockLength)
        {
            if (_oldCount == 0) return false;
            // Probabilities cross-multiplied by both counts: integers instead of fractions
            long delta = 0;
            for (var i = 0; i < Types; i++) delta += Math.Abs((long)_new[i] * _oldCount - (long)_old[i] * _newCount);
            long cutoff = (long)_newCount * 200 / 512 * _oldCount;
            var items = _oldCount + _newCount;
            // Short blocks cost a lot of header: end them only on a clear difference
            if (blockLength < 10_000 && items < 8192) cutoff += cutoff * (8192 - items) / 8192;
            return delta + (long)(blockLength / 4096) * _oldCount >= cutoff;
        }

        public void MergeNew()
        {
            for (var i = 0; i < Types; i++)
            {
                _old[i] += _new[i];
                _new[i] = 0;
            }
            _oldCount += _newCount;
            _newCount = 0;
        }

        /// <summary>
        /// How much does the block differ from the previous one? 0 = hardly … 4 = completely (for the initial costs). Compares the observations
        /// of the finished block (old ones, without a part that already belongs to the next block) with those of the previous block.
        /// </summary>
        public int ChangeFromPrevious()
        {
            var (current, count) = (_oldCount > 0 ? _old : _new, _oldCount > 0 ? _oldCount : _newCount);
            if (_previousCount == 0 || count == 0) return 4;
            long delta = 0;
            for (var i = 0; i < Types; i++) delta += Math.Abs((long)_previous[i] * count - (long)current[i] * _previousCount);
            var cutoff = (long)_previousCount * count * 200 / 512;
            return delta > 3 * cutoff ? 4 : 4 * delta > 9 * cutoff ? 3 : 2 * delta > 3 * cutoff ? 2 : 2 * delta > cutoff ? 1 : 0;
        }

        /// <summary>Block written: remembers its observations; <paramref name="keepNew"/>: the new ones already belong to the next block.</summary>
        public void NextBlock(bool keepNew)
        {
            if (!keepNew) MergeNew();
            Array.Copy(_old, _previous, Types);
            _previousCount = _oldCount;
            Array.Clear(_old);
            _oldCount = 0;
            if (keepNew) return;
            Array.Clear(_new);
            _newCount = 0;
        }
    }

    // ------------------------------------------------------------ Matches per position

    /// <summary>Matches (length, distance) per position of the current block and of the part behind it that was already searched.</summary>
    private sealed class MatchCache(int capacity) : IDisposable
    {
        private int[] _first = ArrayPool<int>.Shared.Rent(capacity + 1);
        private ushort[] _lengths = ArrayPool<ushort>.Shared.Rent(capacity * 4), _distances = ArrayPool<ushort>.Shared.Rent(capacity * 4);
        private int _positions, _total;

        public void Add(ReadOnlySpan<ushort> lengths, ReadOnlySpan<ushort> distances)
        {
            if (_positions + 2 > _first.Length) Grow(ref _first, _first.Length * 2, _positions + 1);
            if (_total + lengths.Length > _lengths.Length)
            {
                var size = Math.Max(_lengths.Length * 2, _total + lengths.Length);
                Grow(ref _lengths, size, _total);
                Grow(ref _distances, size, _total);
            }
            _first[_positions] = _total;
            lengths.CopyTo(_lengths.AsSpan(_total));
            distances.CopyTo(_distances.AsSpan(_total));
            _total += lengths.Length;
            _positions++;
            _first[_positions] = _total;
        }

        /// <summary>Copies the positions from <paramref name="count"/> on into a new cache (they already belong to the next block); this cache stays as it is.</summary>
        public MatchCache SplitAt(int count, int capacity)
        {
            var tail = new MatchCache(Math.Max(capacity, _positions - count + 1));
            for (var i = count; i < _positions; i++)
                tail.Add(_lengths.AsSpan(_first[i], _first[i + 1] - _first[i]), _distances.AsSpan(_first[i], _first[i + 1] - _first[i]));
            return tail;
        }

        /// <summary>Drops the first <paramref name="count"/> positions (written block); the rest moves to the front.</summary>
        public void DropFront(int count)
        {
            var offset = _first[count];
            var rest = _positions - count;
            Array.Copy(_lengths, offset, _lengths, 0, _total - offset);
            Array.Copy(_distances, offset, _distances, 0, _total - offset);
            for (var i = 0; i <= rest; i++) _first[i] = _first[count + i] - offset;
            _positions = rest;
            _total -= offset;
        }

        private static void Grow<T>(ref T[] array, int size, int used)
        {
            var larger = ArrayPool<T>.Shared.Rent(size);
            Array.Copy(array, larger, used);
            ArrayPool<T>.Shared.Return(array);
            array = larger;
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(_first);
            ArrayPool<ushort>.Shared.Return(_lengths);
            ArrayPool<ushort>.Shared.Return(_distances);
        }

        public int First(int position) => _first[position];
        public ReadOnlySpan<ushort> Lengths => _lengths;
        public ReadOnlySpan<ushort> Distances => _distances;
    }

    // ------------------------------------------------------------ Finding repeats

    /// <summary>
    /// Binary tree per hash of the first four bytes: inserting a position yields all earlier matches of increasing length as a
    /// by-product. In addition, for length 3, the last two positions per hash of the first three bytes.
    /// </summary>
    private sealed class MatchFinder
    {
        private int _hashBits, _mask;
        private byte[] _data = [];
        private readonly int[] _head4 = new int[1 << 16], _head3 = new int[2 << 16], _child = new int[2 * Window];

        /// <summary>One per thread (1 MB), reused; allocating the tables anew on every call allocated 18 GB instead of 1.7 GB across the test corpus.</summary>
        [ThreadStatic] private static MatchFinder? _shared;

        /// <summary>
        /// Prepares the finder for <paramref name="data"/>: hash and window sized by the input (full size only from 32 KB on); only the used
        /// part of the tables is reset.
        /// </summary>
        public static MatchFinder For(byte[] data)
        {
            var finder = _shared ??= new MatchFinder();
            finder._data = data;
            finder._hashBits = Math.Clamp(64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)Math.Max(1, data.Length - 1)), 8, 16);
            finder._mask = (int)Math.Min(Window, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, data.Length))) - 1;
            finder._head4.AsSpan(0, 1 << finder._hashBits).Fill(Nil);
            finder._head3.AsSpan(0, 2 << finder._hashBits).Fill(Nil);
            return finder;
        }

        /// <summary>Counts equal bytes from <paramref name="length"/> on, eight at a time (the first differing position from the XOR).</summary>
        /// <remarks>Without bounds checks (hottest loop): a &lt; b and b + maxLength ≤ length of the data, and only bytes before that are read.</remarks>
        private static int Extend(ref byte d, int a, int b, int length, int maxLength)
        {
            while (length + 8 <= maxLength)
            {
                var x = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref d, a + length)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref d, b + length));
                if (x != 0)
                    return length + (BitConverter.IsLittleEndian ? System.Numerics.BitOperations.TrailingZeroCount(x) : System.Numerics.BitOperations.LeadingZeroCount(x)) / 8;
                length += 8;
            }
            while (length < maxLength && Unsafe.Add(ref d, a + length) == Unsafe.Add(ref d, b + length)) length++;
            return length;
        }

        /// <summary>
        /// Inserts position <paramref name="pos"/>; with <paramref name="record"/>, writes the matches (length, distance) of increasing length
        /// into the buffers. Returns the number of matches.
        /// </summary>
        public int Find(int pos, bool record, Span<ushort> lengths, Span<ushort> distances)
        {
            var d = _data;
            var maxLength = Math.Min(MaxMatch, d.Length - pos);
            if (maxLength < MinMatch) return 0;
            var count = 0;
            var best = MinMatch - 1;
            var cutoff = pos - Window;

            var h3 = 2 * (int)(((uint)(d[pos] | d[pos + 1] << 8 | d[pos + 2] << 16) * 0x9E3779B1u) >> (32 - _hashBits));
            var (c3, c3b) = (_head3[h3], _head3[h3 + 1]);
            (_head3[h3], _head3[h3 + 1]) = (pos, c3);
            if (record)
            {
                var found = c3 > cutoff && d[c3] == d[pos] && d[c3 + 1] == d[pos + 1] && d[c3 + 2] == d[pos + 2] ? c3
                    : c3b > cutoff && d[c3b] == d[pos] && d[c3b + 1] == d[pos + 1] && d[c3b + 2] == d[pos + 2] ? c3b : -1;
                if (found >= 0)
                {
                    lengths[0] = MinMatch;
                    distances[0] = (ushort)(pos - found);
                    count = 1;
                    best = MinMatch;
                }
            }
            if (maxLength < 4) return count;

            var h4 = (int)((BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos)) * 0x1E35A7BDu) >> (32 - _hashBits));
            var current = _head4[h4];
            _head4[h4] = pos;
            var lessSlot = 2 * (pos & _mask);
            var greaterSlot = lessSlot + 1;
            int bestLess = 0, bestGreater = 0;
            var nice = Math.Min(NiceLength, maxLength);
            // Without bounds checks (hottest loop): cutoff < current < pos, length < nice ≤ maxLength ≤ length − pos, so current + length and
            // pos + length lie within the array; the nodes (number & _mask, times 2, plus 1) lie within the child array (twice the window)
            ref var data = ref MemoryMarshal.GetArrayDataReference(d);
            ref var child = ref MemoryMarshal.GetArrayDataReference(_child);
            for (var depth = MaxDepth; ; depth--)
            {
                if (current <= cutoff || depth == 0)
                {
                    Unsafe.Add(ref child, lessSlot) = Unsafe.Add(ref child, greaterSlot) = Nil;
                    return count;
                }
                var length = Math.Min(bestLess, bestGreater);
                if (Unsafe.Add(ref data, current + length) == Unsafe.Add(ref data, pos + length))
                {
                    length = Extend(ref data, current, pos, length + 1, maxLength);
                    if (length > best)
                    {
                        best = length;
                        if (record && count < lengths.Length)
                        {
                            lengths[count] = (ushort)length;
                            distances[count] = (ushort)(pos - current);
                            count++;
                        }
                    }
                    if (length >= nice)
                    {
                        // Long enough (or equal up to the end): take over the children of the old node
                        Unsafe.Add(ref child, lessSlot) = Unsafe.Add(ref child, 2 * (current & _mask));
                        Unsafe.Add(ref child, greaterSlot) = Unsafe.Add(ref child, 2 * (current & _mask) + 1);
                        return count;
                    }
                }
                if (Unsafe.Add(ref data, current + length) < Unsafe.Add(ref data, pos + length))
                {
                    Unsafe.Add(ref child, lessSlot) = current;
                    lessSlot = 2 * (current & _mask) + 1;
                    current = Unsafe.Add(ref child, lessSlot);
                    bestLess = length;
                }
                else
                {
                    Unsafe.Add(ref child, greaterSlot) = current;
                    greaterSlot = 2 * (current & _mask);
                    current = Unsafe.Add(ref child, greaterSlot);
                    bestGreater = length;
                }
            }
        }
    }

    // ------------------------------------------------------------ Optimising and writing a block

    private sealed class Optimizer(int capacity) : IDisposable
    {
        // Shortest path: cost up to the position and the step to it (length 1 = literal); rented
        private readonly int[] _cost = ArrayPool<int>.Shared.Rent(capacity + 1);
        private readonly ushort[] _stepLength = ArrayPool<ushort>.Shared.Rent(capacity + 1), _stepDistance = ArrayPool<ushort>.Shared.Rent(capacity + 1);

        // Cost model in 1/16 bit (lengths including their extra bits, distance symbols including theirs)
        private int[] _literalCost = new int[256], _lengthCost = new int[MaxMatch + 1], _distCost = new int[30];
        private int[] _savedLiteral = new int[256], _savedLength = new int[MaxMatch + 1], _savedDist = new int[30];

        // Costs with which the previous block was parsed (initial costs of the next)
        private readonly int[] _prevLiteral = new int[256], _prevLength = new int[MaxMatch + 1], _prevDist = new int[30];

        // Result of a pass: steps (from the end) and frequencies
        private readonly uint[] _sequence = ArrayPool<uint>.Shared.Rent(capacity + 1);   // length | distance << 16, from the end
        private int _steps;

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(_cost);
            ArrayPool<ushort>.Shared.Return(_stepLength);
            ArrayPool<ushort>.Shared.Return(_stepDistance);
            ArrayPool<uint>.Shared.Return(_sequence);
        }
        private readonly int[] _litFreq = new int[286], _distFreq = new int[30];

        /// <summary>Optimises one block and writes it.</summary>
        /// <param name="input">The whole input.</param>
        /// <param name="start">Start of the block in <paramref name="input"/>.</param>
        /// <param name="end">End of the block (exclusive).</param>
        /// <param name="cache">Matches per position, starting at <paramref name="start"/>.</param>
        /// <param name="change">How much the block differs from the previous one (<see cref="SplitStats.ChangeFromPrevious"/>, 4 = first block).</param>
        /// <param name="final">Whether this is the last block (BFINAL).</param>
        /// <param name="output">Bit writer for the result.</param>
        public void Run(byte[] input, int start, int end, MatchCache cache, int change, bool final, BitWriter output)
        {
            var n = end - start;
            // Alternative candidate 1: literals only (some data compresses best this way)
            Array.Clear(_litFreq);
            Array.Clear(_distFreq);
            foreach (var b in input.AsSpan(start, n)) _litFreq[b]++;
            _litFreq[256] = 1;
            var literalsOnly = Plan(_litFreq, _distFreq, n).Bits;

            // Alternative candidate 2: static block with its own parsing (small blocks)
            var fixedBits = long.MaxValue;
            if (n <= StaticUpTo)
            {
                FixedCosts();
                ShortestPath(input, start, n, cache);
                Count(input, start);
                fixedBits = FixedBits(_litFreq, _distFreq);
            }

            // Optimal parsing: passes as long as it improves; the best one wins
            StartCosts(input, start, n, cache, change);
            var best = long.MaxValue;
            var lastBits = long.MaxValue;
            for (var pass = 0; pass < MaxPasses; pass++)
            {
                ShortestPath(input, start, n, cache);
                Count(input, start);
                lastBits = DynamicBits(_litFreq, _distFreq);
                if (lastBits >= best) break;
                var gain = best - lastBits;
                best = lastBits;
                (_savedLiteral, _literalCost) = (_literalCost, _savedLiteral);
                (_savedLength, _lengthCost) = (_lengthCost, _savedLength);
                (_savedDist, _distCost) = (_distCost, _savedDist);
                CostsFromCodes(_litFreq, _distFreq);
                if (gain < best * MinGainPerTenThousand / 10_000) break;
            }
            if (Math.Min(literalsOnly, fixedBits) < best)
            {
                if (literalsOnly <= fixedBits)
                {
                    _steps = 0;
                    for (var i = 0; i < n; i++) _sequence[_steps++] = 1;
                    Array.Clear(_litFreq);
                    Array.Clear(_distFreq);
                    foreach (var b in input.AsSpan(start, n)) _litFreq[b]++;
                    _litFreq[256] = 1;
                }
                else
                {
                    FixedCosts();
                    ShortestPath(input, start, n, cache);
                    Count(input, start);
                }
            }
            else if (lastBits != best)
            {
                // The best was an earlier pass: run again with its costs
                Array.Copy(_savedLiteral, _literalCost, 256);
                Array.Copy(_savedLength, _lengthCost, MaxMatch + 1);
                Array.Copy(_savedDist, _distCost, 30);
                ShortestPath(input, start, n, cache);
                Count(input, start);
            }
            Array.Copy(_literalCost, _prevLiteral, 256);
            Array.Copy(_lengthCost, _prevLength, MaxMatch + 1);
            Array.Copy(_distCost, _prevDist, 30);
            Write(input, start, end, final, output);
        }

        /// <summary>
        /// Initial costs: literals by the number of byte values used, lengths and distances by the match share of a greedy parse
        /// (−log₂ of the probability); from the second block on mixed with the costs of the previous block, the more the more similar it is.
        /// </summary>
        private void StartCosts(byte[] input, int start, int n, MatchCache cache, int change)
        {
            var used = UsedLiterals(input.AsSpan(start, n));
            var minLength = used < 6 ? 9 : used < 8 ? 8 : used < 10 ? 7 : used < 16 ? 6 : used < 45 ? 5 : used < 80 ? 4 : 3;
            long matches = 0, literals = n;
            for (var i = 0; i < n;)
            {
                var last = cache.First(i + 1) - 1;
                var best = last >= cache.First(i) ? cache.Lengths[last] : 0;
                if (best >= minLength)
                {
                    matches++;
                    literals -= best;
                    i += best;
                }
                else i++;
            }
            var p = matches > literals ? 0.75 : matches * 4 > literals ? 0.5 : 0.25;
            var literal = (int)Math.Round(BitCost * (Math.Log2(used) - Math.Log2(1 - p)));
            var lengthSymbol = (int)Math.Round(BitCost * (Math.Log2(28) - Math.Log2(p)));
            var distSymbol = (int)Math.Round(BitCost * Math.Log2(30));
            static int Mix(int previous, int fresh, int change) => change switch
            {
                0 => (fresh + 3 * previous) / 4,
                1 => (fresh + previous) / 2,
                2 => (5 * fresh + 3 * previous) / 8,
                3 => (3 * fresh + previous) / 4,
                _ => fresh,
            };
            for (var c = 0; c < 256; c++) _literalCost[c] = Mix(_prevLiteral[c], literal, change);
            for (var l = MinMatch; l <= MaxMatch; l++)
                _lengthCost[l] = Mix(_prevLength[l], lengthSymbol + BitCost * LengthExtra[LengthSymbol[l]], change);
            for (var s = 0; s < 30; s++) _distCost[s] = Mix(_prevDist[s], distSymbol + BitCost * DistExtra[s], change);
        }

        /// <summary>Costs of the static codes (RFC 1951, 3.2.6).</summary>
        private void FixedCosts()
        {
            for (var c = 0; c < 256; c++) _literalCost[c] = BitCost * FixedLiteralLength(c);
            for (var l = MinMatch; l <= MaxMatch; l++)
                _lengthCost[l] = BitCost * (FixedLiteralLength(257 + LengthSymbol[l]) + LengthExtra[LengthSymbol[l]]);
            for (var s = 0; s < 30; s++) _distCost[s] = BitCost * (5 + DistExtra[s]);
        }

        /// <summary>Costs from the code lengths the frequencies would yield; symbols never used get fixed, expensive values.</summary>
        private void CostsFromCodes(int[] litFreq, int[] distFreq)
        {
            Span<byte> lit = stackalloc byte[288];
            Span<byte> dist = stackalloc byte[30];
            Huffman.Lengths(litFreq, 15, lit);
            Huffman.Lengths(distFreq, 15, dist);
            for (var c = 0; c < 256; c++) _literalCost[c] = BitCost * (lit[c] == 0 ? 13 : lit[c]);
            for (var l = MinMatch; l <= MaxMatch; l++)
            {
                var symbol = LengthSymbol[l];
                _lengthCost[l] = BitCost * ((lit[257 + symbol] == 0 ? 13 : lit[257 + symbol]) + LengthExtra[symbol]);
            }
            for (var s = 0; s < 30; s++) _distCost[s] = BitCost * ((dist[s] == 0 ? 10 : dist[s]) + DistExtra[s]);
        }

        private void ShortestPath(byte[] input, int start, int n, MatchCache cache)
        {
            var cost = _cost;
            Array.Fill(cost, int.MaxValue, 0, n + 1);
            cost[0] = 0;
            var lengths = cache.Lengths;
            var distances = cache.Distances;
            // Without bounds checks (hottest loop): targets i + l with l ≤ left = n − i, so at most n, and the arrays hold at least n + 1;
            // lengths 3 … 258 lie within the length cost array
            ref var costs = ref MemoryMarshal.GetArrayDataReference(cost);
            ref var stepLength = ref MemoryMarshal.GetArrayDataReference(_stepLength);
            ref var stepDistance = ref MemoryMarshal.GetArrayDataReference(_stepDistance);
            ref var lengthCost = ref MemoryMarshal.GetArrayDataReference(_lengthCost);
            for (var i = 0; i < n; i++)
            {
                var here = Unsafe.Add(ref costs, i);
                var literal = here + _literalCost[input[start + i]];
                if (literal < Unsafe.Add(ref costs, i + 1))
                {
                    Unsafe.Add(ref costs, i + 1) = literal;
                    Unsafe.Add(ref stepLength, i + 1) = 1;
                }
                var previous = MinMatch - 1;
                var left = n - i;
                for (int k = cache.First(i), last = cache.First(i + 1); k < last && previous < left; k++)
                {
                    var length = Math.Min((int)lengths[k], left);
                    var distance = distances[k];
                    var baseCost = here + _distCost[DistSymbol(distance)];
                    ref var targets = ref Unsafe.Add(ref costs, i);
                    for (var l = previous + 1; l <= length; l++)
                    {
                        var c = baseCost + Unsafe.Add(ref lengthCost, l);
                        if (c < Unsafe.Add(ref targets, l))
                        {
                            Unsafe.Add(ref targets, l) = c;
                            Unsafe.Add(ref stepLength, i + l) = (ushort)l;
                            Unsafe.Add(ref stepDistance, i + l) = distance;
                        }
                    }
                    previous = length;
                }
            }
            // Back from the end
            _steps = 0;
            for (var i = n; i > 0;)
            {
                var length = _stepLength[i];
                _sequence[_steps++] = length | (uint)(length == 1 ? 0 : _stepDistance[i]) << 16;
                i -= length;
            }
        }

        private void Count(byte[] input, int start)
        {
            Array.Clear(_litFreq);
            Array.Clear(_distFreq);
            var pos = start;
            for (var k = _steps - 1; k >= 0; k--)
            {
                var (length, distance) = ((ushort)_sequence[k], (ushort)(_sequence[k] >> 16));
                if (length == 1) _litFreq[input[pos]]++;
                else
                {
                    _litFreq[257 + LengthSymbol[length]]++;
                    _distFreq[DistSymbol(distance)]++;
                }
                pos += length;
            }
            _litFreq[256] = 1;
        }

        private void Write(byte[] input, int start, int end, bool final, BitWriter output)
        {
            var (_, kind) = Plan(_litFreq, _distFreq, end - start);
            if (kind == 0)
            {
                for (var at = start; at < end;)
                {
                    var length = Math.Min(65535, end - at);
                    output.Bits(final && at + length == end ? 1u : 0u, 1);
                    output.Bits(0, 2);
                    output.Align();
                    output.Bits((uint)length, 16);
                    output.Bits((uint)(~length & 0xFFFF), 16);
                    for (var i = 0; i < length; i++) output.Byte(input[at + i]);
                    at += length;
                }
                return;
            }
            var lit = new byte[288];
            var dist = new byte[30];
            Tables(_litFreq, _distFreq, lit, dist);
            output.Bits(final ? 1u : 0u, 1);
            if (kind == 1)
            {
                output.Bits(1, 2);
                for (var s = 0; s < 288; s++) lit[s] = (byte)FixedLiteralLength(s);
                Array.Fill(dist, (byte)5);
            }
            else
            {
                output.Bits(2, 2);
                WriteHeader(output, lit, dist);
            }
            var litCodes = Huffman.Codes(lit);
            var distCodes = Huffman.Codes(dist);
            var pos = start;
            for (var k = _steps - 1; k >= 0; k--)
            {
                var (length, distance) = ((ushort)_sequence[k], (ushort)(_sequence[k] >> 16));
                if (length == 1) output.Bits(litCodes[input[pos]], lit[input[pos]]);
                else
                {
                    var ls = LengthSymbol[length];
                    output.Bits(litCodes[257 + ls], lit[257 + ls]);
                    output.Bits((uint)(length - LengthBase[ls]), LengthExtra[ls]);
                    var ds = DistSymbol(distance);
                    output.Bits(distCodes[ds], dist[ds]);
                    output.Bits((uint)(distance - DistBase[ds]), DistExtra[ds]);
                }
                pos += length;
            }
            output.Bits(litCodes[256], lit[256]);
        }
    }

    /// <summary>Code lengths of a dynamic block from the frequencies (at least one distance code, never a single one alone).</summary>
    private static void Tables(ReadOnlySpan<int> litFreq, ReadOnlySpan<int> distFreq, Span<byte> lit, Span<byte> dist)
    {
        Huffman.Lengths(litFreq, 15, lit);
        Huffman.Lengths(distFreq, 15, dist);
        // Without distances, codes of length 1 suffice (RFC 1951, 3.2.7); if only one is used, a second one is added so that the code
        // is complete
        var usedDist = 0;
        foreach (var x in dist)
            if (x > 0) usedDist++;
        if (usedDist == 0) dist[0] = dist[1] = 1;
        else if (usedDist == 1) dist[dist[0] == 0 ? 0 : 1] = 1;
    }

    private static long Extra(ReadOnlySpan<int> litFreq, ReadOnlySpan<int> distFreq)
    {
        long extra = 0;
        for (var s = 257; s < 286; s++) extra += (long)litFreq[s] * LengthExtra[s - 257];
        for (var s = 0; s < 30; s++) extra += (long)distFreq[s] * DistExtra[s];
        return extra;
    }

    /// <summary>Length in bits of a block with dynamic tables (header, data, extra bits); allocation-free, because it runs on every pass.</summary>
    private static long DynamicBits(ReadOnlySpan<int> litFreq, ReadOnlySpan<int> distFreq)
    {
        Span<byte> lit = stackalloc byte[288];
        Span<byte> dist = stackalloc byte[30];
        Tables(litFreq, distFreq, lit, dist);
        var bits = HeaderBits(lit, dist) + 3 + Extra(litFreq, distFreq);
        for (var s = 0; s < 286; s++) bits += (long)litFreq[s] * lit[s];
        for (var s = 0; s < 30; s++) bits += (long)distFreq[s] * dist[s];
        return bits;
    }

    /// <summary>Length in bits of a block with the static codes.</summary>
    private static long FixedBits(ReadOnlySpan<int> litFreq, ReadOnlySpan<int> distFreq)
    {
        var bits = 3 + Extra(litFreq, distFreq);
        for (var s = 0; s < 286; s++) bits += (long)litFreq[s] * FixedLiteralLength(s);
        for (var s = 0; s < 30; s++) bits += distFreq[s] * 5L;
        return bits;
    }

    /// <summary>Smallest block type and its length in bits: 0 = stored, 1 = static, 2 = dynamic tables.</summary>
    private static (long Bits, int Kind) Plan(ReadOnlySpan<int> litFreq, ReadOnlySpan<int> distFreq, int n)
    {
        var dynamicBits = DynamicBits(litFreq, distFreq);
        var fixedBits = FixedBits(litFreq, distFreq);
        var storedBits = (n + 65534) / 65535 * 40L + n * 8L + 7;
        return storedBits < dynamicBits && storedBits < fixedBits ? (storedBits, 0) : fixedBits <= dynamicBits ? (fixedBits, 1) : (dynamicBits, 2);
    }

    private static int FixedLiteralLength(int s) => s < 144 ? 8 : s < 256 ? 9 : s < 280 ? 7 : 8;

    /// <summary>
    /// Run-length codes the code lengths of both tables (RFC 1951, 3.2.7): 16 = previous one 3 … 6 times, 17 = zero 3 … 10 times,
    /// 18 = zero 11 … 138 times. Returns the number of symbols.
    /// </summary>
    private static int RunLengths(ReadOnlySpan<byte> lit, ReadOnlySpan<byte> dist, Span<byte> symbols, Span<byte> extras, out int hlit, out int hdist)
    {
        hlit = 286;
        while (hlit > 257 && lit[hlit - 1] == 0) hlit--;
        hdist = 30;
        while (hdist > 1 && dist[hdist - 1] == 0) hdist--;
        Span<byte> all = stackalloc byte[316];
        lit[..hlit].CopyTo(all);
        dist[..hdist].CopyTo(all[hlit..]);
        all = all[..(hlit + hdist)];
        var count = 0;
        void Add(int symbol, int extra, Span<byte> s, Span<byte> e)
        {
            s[count] = (byte)symbol;
            e[count] = (byte)extra;
            count++;
        }
        for (var i = 0; i < all.Length;)
        {
            var value = all[i];
            var run = 1;
            while (i + run < all.Length && all[i + run] == value) run++;
            var left = run;
            if (value == 0)
            {
                while (left >= 11)
                {
                    var r = Math.Min(left, 138);
                    Add(18, r - 11, symbols, extras);
                    left -= r;
                }
                if (left >= 3)
                {
                    Add(17, left - 3, symbols, extras);
                    left = 0;
                }
            }
            else
            {
                Add(value, 0, symbols, extras);
                left--;
                while (left >= 3)
                {
                    var r = Math.Min(left, 6);
                    Add(16, r - 3, symbols, extras);
                    left -= r;
                }
            }
            for (; left > 0; left--) Add(value, 0, symbols, extras);
            i += run;
        }
        return count;
    }

    /// <summary>Code lengths of the code-length code (at most 7 bits, never a single code alone) and how many of them are written.</summary>
    private static int CodeLengthCode(ReadOnlySpan<byte> symbols, Span<byte> clen)
    {
        Span<int> freq = stackalloc int[19];
        freq.Clear();
        foreach (var s in symbols) freq[s]++;
        Huffman.Lengths(freq, 7, clen);
        var used = 0;
        foreach (var l in clen)
            if (l > 0) used++;
        if (used == 1) clen[clen[0] > 0 ? 1 : 0] = 1;
        var hclen = 19;
        while (hclen > 4 && clen[CodeLengthOrder[hclen - 1]] == 0) hclen--;
        return hclen;
    }

    /// <summary>Length in bits of the header of a dynamic block (without the three bits of the block type).</summary>
    private static long HeaderBits(ReadOnlySpan<byte> lit, ReadOnlySpan<byte> dist)
    {
        Span<byte> symbols = stackalloc byte[316];
        Span<byte> extras = stackalloc byte[316];
        var count = RunLengths(lit, dist, symbols, extras, out _, out _);
        Span<byte> clen = stackalloc byte[19];
        var hclen = CodeLengthCode(symbols[..count], clen);
        long bits = 14 + 3 * hclen;
        foreach (var s in symbols[..count]) bits += clen[s] + (s == 16 ? 2 : s == 17 ? 3 : s == 18 ? 7 : 0);
        return bits;
    }

    /// <summary>Writes the header of a dynamic block (RFC 1951, 3.2.7).</summary>
    private static void WriteHeader(BitWriter output, ReadOnlySpan<byte> lit, ReadOnlySpan<byte> dist)
    {
        Span<byte> symbols = stackalloc byte[316];
        Span<byte> extras = stackalloc byte[316];
        var count = RunLengths(lit, dist, symbols, extras, out var hlit, out var hdist);
        var clen = new byte[19];
        var hclen = CodeLengthCode(symbols[..count], clen);
        var codes = Huffman.Codes(clen);
        output.Bits((uint)(hlit - 257), 5);
        output.Bits((uint)(hdist - 1), 5);
        output.Bits((uint)(hclen - 4), 4);
        for (var i = 0; i < hclen; i++) output.Bits(clen[CodeLengthOrder[i]], 3);
        for (var i = 0; i < count; i++)
        {
            var s = symbols[i];
            output.Bits(codes[s], clen[s]);
            if (s == 16) output.Bits(extras[i], 2);
            else if (s == 17) output.Bits(extras[i], 3);
            else if (s == 18) output.Bits(extras[i], 7);
        }
    }

    // ------------------------------------------------------------ Huffman

    internal static class Huffman
    {
        /// <summary>Optimal code lengths of at most <paramref name="limit"/> bits (package-merge if needed); a single symbol gets length 1.</summary>
        public static void Lengths(ReadOnlySpan<int> freq, int limit, Span<byte> lengths)
        {
            lengths.Clear();
            // Sorted by frequency (ties by symbol), without allocations, because this runs several times per block and pass
            Span<long> keys = stackalloc long[freq.Length];
            var count = 0;
            for (var s = 0; s < freq.Length; s++)
                if (freq[s] > 0) keys[count++] = (long)freq[s] << 9 | (uint)s;
            if (count == 0) return;
            if (count == 1)
            {
                lengths[(int)(keys[0] & 511)] = 1;
                return;
            }
            keys = keys[..count];
            keys.Sort();
            Span<int> sorted = stackalloc int[count];
            for (var i = 0; i < count; i++) sorted[i] = (int)(keys[i] & 511);
            if (Plain(freq, sorted, limit, lengths)) return;
            PackageMerge(freq.ToArray(), [.. sorted], limit, lengths);
        }

        /// <summary>Length-limited codes (package-merge), only used when ordinary Huffman coding would give codes that are too long.</summary>
        private static void PackageMerge(int[] freq, List<int> leaves, int limit, Span<byte> lengths)
        {
            // Nodes: weight and either a leaf (symbol) or two children (package)
            var weight = new List<long>();
            var leaf = new List<int>();
            var childA = new List<int>();
            var childB = new List<int>();
            int Add(long w, int symbol, int a, int b)
            {
                weight.Add(w);
                leaf.Add(symbol);
                childA.Add(a);
                childB.Add(b);
                return weight.Count - 1;
            }
            var leafNodes = leaves.Select(s => Add(freq[s], s, -1, -1)).ToArray();
            var list = leafNodes.ToList();
            for (var level = 1; level < limit; level++)
            {
                var packages = new List<int>();
                for (var i = 0; i + 1 < list.Count; i += 2) packages.Add(Add(weight[list[i]] + weight[list[i + 1]], -1, list[i], list[i + 1]));
                var merged = new List<int>(leafNodes.Length + packages.Count);
                int p = 0, q = 0;
                while (p < leafNodes.Length || q < packages.Count)
                    merged.Add(q >= packages.Count || p < leafNodes.Length && weight[leafNodes[p]] <= weight[packages[q]] ? leafNodes[p++] : packages[q++]);
                list = merged;
            }
            // The first 2n − 2 entries: the number of times each leaf occurs in them is its code length
            var stack = new Stack<int>();
            for (var i = 0; i < 2 * leaves.Count - 2; i++)
            {
                stack.Push(list[i]);
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    if (leaf[node] >= 0) lengths[leaf[node]]++;
                    else
                    {
                        stack.Push(childA[node]);
                        stack.Push(childB[node]);
                    }
                }
            }
        }

        /// <summary>
        /// Ordinary Huffman coding with two queues (leaves sorted, inner nodes are created in sorted order), in linear time. Succeeds only if
        /// no code becomes longer than <paramref name="limit"/> (almost always with 15 bits); otherwise returns false and package-merge is used.
        /// </summary>
        private static bool Plain(ReadOnlySpan<int> freq, ReadOnlySpan<int> leaves, int limit, Span<byte> lengths)
        {
            var n = leaves.Length;
            Span<long> weight = stackalloc long[2 * n];
            Span<int> parent = stackalloc int[2 * n];
            Span<byte> depth = stackalloc byte[2 * n];
            for (var i = 0; i < n; i++) weight[i] = freq[leaves[i]];
            int leaf = 0, inner = n;
            for (var next = n; next < 2 * n - 1; next++)
            {
                int Smallest(Span<long> w, ref int l, ref int i, int end) =>
                    i < end && (l >= n || w[i] < w[l]) ? i++ : l++;
                var a = Smallest(weight, ref leaf, ref inner, next);
                var b = Smallest(weight, ref leaf, ref inner, next);
                weight[next] = weight[a] + weight[b];
                parent[a] = parent[b] = next;
            }
            depth[2 * n - 2] = 0;
            for (var node = 2 * n - 3; node >= 0; node--)
            {
                depth[node] = (byte)(depth[parent[node]] + 1);
                if (depth[node] > limit) return false;
            }
            for (var i = 0; i < n; i++) lengths[leaves[i]] = depth[i];
            return true;
        }

        /// <summary>Canonical codes (RFC 1951, 3.2.2), already bit-reversed for writing from the least significant bit.</summary>
        public static uint[] Codes(byte[] lengths)
        {
            Span<int> count = stackalloc int[16];
            Span<int> next = stackalloc int[16];
            count.Clear();
            foreach (var l in lengths)
                if (l > 0) count[l]++;
            var code = 0;
            for (var bits = 1; bits < 16; bits++)
            {
                code = (code + count[bits - 1]) << 1;
                next[bits] = code;
            }
            var codes = new uint[lengths.Length];
            for (var s = 0; s < lengths.Length; s++)
            {
                int length = lengths[s];
                if (length == 0) continue;
                var c = next[length]++;
                uint reversed = 0;
                for (var b = 0; b < length; b++) reversed |= (uint)((c >> b) & 1) << (length - 1 - b);
                codes[s] = reversed;
            }
            return codes;
        }
    }

    // ------------------------------------------------------------ Writing bits

    private sealed class BitWriter(int capacity)
    {
        private byte[] _buffer = new byte[Math.Max(64, capacity)];
        private int _length;
        private ulong _bits;
        private int _count;

        public void Bits(uint value, int count)
        {
            if (count == 0) return;
            _bits |= (ulong)value << _count;
            _count += count;
            while (_count >= 8)
            {
                Put((byte)_bits);
                _bits >>= 8;
                _count -= 8;
            }
        }

        public void Align()
        {
            if (_count > 0) Put((byte)_bits);
            (_bits, _count) = (0, 0);
        }

        /// <summary>Writes a whole byte (only at byte boundaries: header, stored blocks, checksum).</summary>
        public void Byte(byte value) => Put(value);

        private void Put(byte value)
        {
            if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            _buffer[_length++] = value;
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
    }
}
