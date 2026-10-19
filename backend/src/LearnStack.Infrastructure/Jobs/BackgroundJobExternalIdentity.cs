using System.Security.Cryptography;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Stable logical-operation identity; no attempt, process, lease or wall clock enters its derivation.</summary>
internal static class BackgroundJobExternalIdentity
{
    internal static Guid OperationId(BackgroundJobId jobId, string canonicalPlanFingerprint)
    {
        if (!jobId.IsInitialized() || jobId.Value.Version != 7)
            throw new ArgumentException("The operation requires its durable UUIDv7 job identity.", nameof(jobId));
        ArgumentNullException.ThrowIfNull(canonicalPlanFingerprint);
        if (canonicalPlanFingerprint.Length != 64
            || canonicalPlanFingerprint.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The operation requires its canonical SHA-256 plan fingerprint.", nameof(canonicalPlanFingerprint));

        ReadOnlySpan<byte> domain = "learnstack.background-job.operation.v1\0"u8;
        Span<byte> input = stackalloc byte[domain.Length + 16 + 32];
        domain.CopyTo(input);
        if (!jobId.Value.TryWriteBytes(input.Slice(domain.Length, 16), bigEndian: true, out var written) || written != 16)
            throw new InvalidOperationException("The job identity could not be encoded in network order.");
        Convert.FromHexString(canonicalPlanFingerprint).CopyTo(input[(domain.Length + 16)..]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        digest[6] = (byte)((digest[6] & 0x0f) | 0x80);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest[..16], bigEndian: true);
    }
}
