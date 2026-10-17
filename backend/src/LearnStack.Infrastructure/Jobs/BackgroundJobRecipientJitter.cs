using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Stable microsecond jitter per stored occurrence and route; retries never draw another value.</summary>
internal static class BackgroundJobRecipientJitter
{
    internal static DateTimeOffset DueAt(Guid occurrence, Guid tenant, Guid? organization, DateTimeOffset slot, long maximumMicroseconds)
    {
        if (maximumMicroseconds is < 0 or > 7_200_000_000) throw new ArgumentOutOfRangeException(nameof(maximumMicroseconds));
        if (maximumMicroseconds == 0) return slot;
        var input = occurrence.ToString("D") + "\n" + tenant.ToString("D") + "\n" + organization?.ToString("D");
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(input));
        var microseconds = BinaryPrimitives.ReadUInt64BigEndian(hash) % (ulong)(maximumMicroseconds + 1);
        return slot.AddTicks(checked((long)microseconds * TimeSpan.TicksPerMicrosecond));
    }
}
