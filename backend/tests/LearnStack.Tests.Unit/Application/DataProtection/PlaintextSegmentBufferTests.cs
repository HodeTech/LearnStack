using System.Buffers;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.DataProtection;
using LearnStack.SharedKernel.DataProtection;
using Xunit;

namespace LearnStack.Tests.Unit.Application.DataProtection;

/// <summary>
/// The bundle's plaintext segments are rented, not allocated per chunk: exactly one chunk of each rented array is used,
/// and every rented array is overwritten in full before it goes back to the pool, on every path. The buffer is internal
/// to <c>LearnStack.Application</c>, which has no friend assembly, so it is reached through its declared constructor.
/// </summary>
public sealed class PlaintextSegmentBufferTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("overflowed")]
    [InlineData("disposed_twice")]
    [InlineData("disposed_while_reading")]
    public void Every_rented_segment_is_cleared_in_full_before_it_is_returned(string path)
    {
        var pool = new RecordingPool();
        var limit = path == "overflowed" ? DataRequestLimits.ChunkPlaintextBytes + 10 : 3L * DataRequestLimits.ChunkPlaintextBytes;
        var buffer = Create(limit, pool);
        var writer = (IBufferWriter<byte>)buffer;
        var written = 0;
        while (written < 2 * DataRequestLimits.ChunkPlaintextBytes + 5)
        {
            var span = writer.GetSpan(4096);
            var count = Math.Min(4096, 2 * DataRequestLimits.ChunkPlaintextBytes + 5 - written);
            span[..count].Fill(0x5a);
            writer.Advance(count);
            written += count;
        }

        Overflowed(buffer).Should().Be(path == "overflowed");
        if (path is "completed" or "disposed_twice" or "disposed_while_reading")
        {
            var chunks = Chunks(buffer).ToArray();
            chunks.Select(chunk => chunk.Length).Should().Equal(DataRequestLimits.ChunkPlaintextBytes, DataRequestLimits.ChunkPlaintextBytes, 5);
            chunks.Should().OnlyContain(chunk => Only(chunk, 0x5a), "only the bytes written are handed out");
        }

        if (path == "disposed_while_reading")
        {
            var reading = Chunks(buffer).GetEnumerator();
            reading.MoveNext().Should().BeTrue();
            ((IDisposable)buffer).Dispose();
            FluentActions.Invoking(() => reading.MoveNext()).Should().Throw<ObjectDisposedException>("a reader stops at disposal");
        }
        else
        {
            ((IDisposable)buffer).Dispose();
        }

        if (path == "disposed_twice")
        {
            ((IDisposable)buffer).Dispose();
        }

        pool.Rented.Should().NotBeEmpty().And.OnlyContain(array => array.Length >= DataRequestLimits.ChunkPlaintextBytes);
        pool.Returned.Should().HaveSameCount(pool.Rented);
        pool.Rented.Should().OnlyContain(array => pool.Returned.Count(returned => ReferenceEquals(returned, array)) == 1,
            "every rented array goes back exactly once");
        pool.ClearedOnReturn.Should().OnlyContain(cleared => cleared, "each array was overwritten in full before its return");
        pool.Returned.Should().OnlyContain(array => Only(array, 0));
    }

    [Fact]
    public void A_rented_array_larger_than_a_chunk_and_holding_a_former_renters_bytes_hands_out_only_what_was_written()
    {
        var pool = new RecordingPool(oversize: 4096, stale: 0xee);
        var buffer = Create(DataRequestLimits.ChunkPlaintextBytes * 2L, pool);
        var writer = (IBufferWriter<byte>)buffer;
        var written = 0;
        while (written < DataRequestLimits.ChunkPlaintextBytes + 3)
        {
            writer.GetSpan(3)[..3].Fill(0x11);
            writer.Advance(3);
            written += 3;
        }

        var chunks = Chunks(buffer).ToArray();
        chunks.Select(chunk => chunk.Length).Should().Equal(DataRequestLimits.ChunkPlaintextBytes, written - DataRequestLimits.ChunkPlaintextBytes);
        chunks.Should().OnlyContain(chunk => Only(chunk, 0x11), "no stale byte of a former renter is read");
        ((IDisposable)buffer).Dispose();
        pool.Returned.Should().OnlyContain(array => array.Length == DataRequestLimits.ChunkPlaintextBytes + 4096
            && Only(array, 0), "the whole rented array is cleared, its unused tail included");
    }

    private static bool Only(ReadOnlyMemory<byte> bytes, byte value) => bytes.Span.IndexOfAnyExcept(value) < 0;

    private static readonly Type BufferType = typeof(DataExportBundleBuilder).Assembly
        .GetType("LearnStack.Application.DataProtection.PlaintextSegmentBuffer", throwOnError: true)!;

    private static object Create(long limit, ArrayPool<byte> pool) =>
        BufferType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(long), typeof(ArrayPool<byte>)])!
            .Invoke([limit, pool]);

    private static bool Overflowed(object buffer) => (bool)BufferType.GetProperty("Overflowed")!.GetValue(buffer)!;

    private static IEnumerable<ReadOnlyMemory<byte>> Chunks(object buffer) =>
        (IEnumerable<ReadOnlyMemory<byte>>)BufferType.GetMethod("Chunks")!.Invoke(buffer, [])!;

    /// <summary>A pool that records what it lent and whether each array came back cleared.</summary>
    private sealed class RecordingPool(int oversize = 0, byte stale = 0) : ArrayPool<byte>
    {
        public List<byte[]> Rented { get; } = [];
        public List<byte[]> Returned { get; } = [];
        public List<bool> ClearedOnReturn { get; } = [];

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength + oversize];
            array.AsSpan().Fill(stale);
            Rented.Add(array);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ClearedOnReturn.Add(array.AsSpan().IndexOfAnyExcept((byte)0) < 0);
            Returned.Add(array);
        }
    }
}
