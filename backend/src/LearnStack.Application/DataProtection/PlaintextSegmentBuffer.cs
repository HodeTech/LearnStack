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
/// overwrites the scratch with zeros. Every segment is overwritten with zeros on <see cref="Dispose"/>.
/// </para>
/// <para>
/// <b>The limit never throws inside a writer.</b> Bytes past it are dropped and <see cref="Overflowed"/> is set,
/// so a JSON writer flushing past the ceiling leaves the caller a refusal to read, not a half-written state.
/// </para>
/// </remarks>
internal sealed class PlaintextSegmentBuffer(long limit) : IBufferWriter<byte>, IDisposable
{
    private const int MinimumScratch = 4096;
    private readonly List<byte[]> _segments = [];
    private byte[] _scratch = [];
    private int _tail = DataRequestLimits.ChunkPlaintextBytes;
    private bool _disposed;

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
            if (Overflowed || Length + count > limit)
            {
                Overflowed = true;
                return;
            }

            var source = _scratch.AsSpan(0, count);
            while (!source.IsEmpty)
            {
                if (_tail == DataRequestLimits.ChunkPlaintextBytes)
                {
                    _segments.Add(new byte[DataRequestLimits.ChunkPlaintextBytes]);
                    _tail = 0;
                }

                var take = Math.Min(source.Length, DataRequestLimits.ChunkPlaintextBytes - _tail);
                source[..take].CopyTo(_segments[^1].AsSpan(_tail));
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
        for (var index = 0; index < _segments.Count; index++)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            yield return index == _segments.Count - 1
                ? _segments[index].AsMemory(0, _tail)
                : _segments[index];
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
            CryptographicOperations.ZeroMemory(segment);
        }

        CryptographicOperations.ZeroMemory(_scratch);
        _segments.Clear();
        _scratch = [];
        Length = 0;
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
