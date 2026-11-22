using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Application.DataProtection;

/// <summary>
/// A bounded, append-only plaintext buffer of fixed chunk-sized segments, so the bundle is chunked exactly as
/// the artifact stores it and no single allocation grows with the bundle.
/// </summary>
/// <remarks>
/// <para>
/// A writer is handed one scratch buffer; <see cref="Advance"/> copies what it wrote into the segments and
/// overwrites the scratch with zeros. Each segment is rented from an <see cref="ArrayPool{T}"/> (the shared pool in
/// production) and exactly <see cref="DataRequestLimits.ChunkPlaintextBytes"/> of it is used; on
/// <see cref="Dispose"/> every rented array is overwritten with zeros in full before it is returned, so the pool never
/// hands a later renter the bundle's plaintext. A chunk from <see cref="Chunks"/> is valid only until then.
/// </para>
/// <para>
/// Hygiene is best effort: the buffers this type owns are cleared, the JSON writer's own state and any copy a reader
/// makes are not.
/// </para>
/// <para>
/// <b>The limit never throws inside a writer.</b> Bytes past it are dropped and <see cref="Overflowed"/> is set,
/// so a JSON writer flushing past the ceiling leaves the caller a refusal to read, not a half-written state.
/// </para>
/// </remarks>
internal sealed class PlaintextSegmentBuffer : IBufferWriter<byte>, IDisposable
{
    private const int MinimumScratch = 4096;
    private readonly long _limit;
    private readonly ArrayPool<byte> _pool;
    private readonly List<byte[]> _segments = [];
    private byte[] _scratch = [];
    private int _tail = DataRequestLimits.ChunkPlaintextBytes;
    private bool _disposed;

    public PlaintextSegmentBuffer(long limit)
        : this(limit, ArrayPool<byte>.Shared)
    {
    }

    /// <summary>A buffer over a given pool; the production constructor passes the shared one.</summary>
    internal PlaintextSegmentBuffer(long limit, ArrayPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _limit = limit;
        _pool = pool;
    }

    /// <summary>The bytes stored.</summary>
    public long Length { get; private set; }

    /// <summary>Whether a write passed the limit; the stored bytes are then incomplete and must not be used.</summary>
    public bool Overflowed { get; private set; }

    public Memory<byte> GetMemory(int sizeHint = 0) => Scratch(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => Scratch(sizeHint);

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _scratch.Length);
        try
        {
            if (Overflowed || Length + count > _limit)
            {
                Overflowed = true;
                return;
            }

            var source = _scratch.AsSpan(0, count);
            while (!source.IsEmpty)
            {
                if (_tail == DataRequestLimits.ChunkPlaintextBytes)
                {
                    // A rented array may be larger and may hold a former renter's bytes: only its first chunk-size
                    // bytes are used, each written before it is read.
                    var segment = _pool.Rent(DataRequestLimits.ChunkPlaintextBytes);
                    try
                    {
                        _segments.Add(segment);
                    }
                    catch
                    {
                        Release(segment);
                        throw;
                    }

                    _tail = 0;
                }

                var take = Math.Min(source.Length, DataRequestLimits.ChunkPlaintextBytes - _tail);
                source[..take].CopyTo(_segments[^1].AsSpan(_tail, take));
                _tail += take;
                source = source[take..];
            }

            Length += count;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_scratch.AsSpan(0, count));
        }
    }

    /// <summary>The stored bytes as chunks of exactly the chunk size but the last.</summary>
    public IEnumerable<ReadOnlyMemory<byte>> Chunks()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var index = 0; ; index++)
        {
            // Checked before the bound: a reader that outlives the buffer is told so, never handed a returned array
            // and never shown a shortened bundle as if it had ended.
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index >= _segments.Count)
            {
                yield break;
            }

            yield return index == _segments.Count - 1
                ? _segments[index].AsMemory(0, _tail)
                : _segments[index].AsMemory(0, DataRequestLimits.ChunkPlaintextBytes);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var segment in _segments)
        {
            Release(segment);
        }

        CryptographicOperations.ZeroMemory(_scratch);
        _segments.Clear();
        _scratch = [];
        Length = 0;
    }

    /// <summary>Overwrites the whole rented array, not only the part used, and only then returns it.</summary>
    private void Release(byte[] segment)
    {
        CryptographicOperations.ZeroMemory(segment);
        _pool.Return(segment);
    }

    private byte[] Scratch(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var needed = Math.Max(sizeHint, MinimumScratch);
        if (_scratch.Length < needed)
        {
            CryptographicOperations.ZeroMemory(_scratch);
            _scratch = new byte[(int)Math.Min(Array.MaxLength, (long)BitOperations.RoundUpToPowerOf2((uint)needed))];
        }

        return _scratch;
    }
}
