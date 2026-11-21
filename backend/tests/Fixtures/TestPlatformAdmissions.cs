using LearnStack.SharedKernel.Platform;

namespace LearnStack.Tests.Fixtures;

/// <summary>
/// Test-only mints of operator witnesses and admissions. Production owns the closed minter
/// census (the operator scheme and the gate); a test mints through its own nested subclasses,
/// which the census architecture test does not scan.
/// </summary>
internal static class TestPlatformAdmissions
{
    internal const string Issuer = "https://operator.test/realms/learnstack-hub";

    internal static PlatformOperatorWitness Witness(string subject = "operator-1", string? correlationId = "00-test-trace-01") =>
        WitnessMinter.Create(
            Issuer, PlatformOperatorSubject.Digest(Issuer, subject), "mfa", DateTimeOffset.UtcNow, "operator.test", correlationId);

    internal static PlatformAdmission Admission(
        PlatformOperation? operation = null,
        string? ticket = null,
        string subject = "operator-1",
        string? correlationId = "00-test-trace-01",
        DateTimeOffset? issuedAt = null) =>
        AdmissionMinter.Create(
            Witness(subject, correlationId),
            operation ?? PlatformOperations.KillswitchToggle,
            ticket,
            issuedAt ?? DateTimeOffset.UtcNow);

    private sealed class WitnessMinter : PlatformOperatorWitness.Minter
    {
        internal static PlatformOperatorWitness Create(
            string issuer, string digest, string acr, DateTimeOffset at, string executor, string? correlationId) =>
            Mint(issuer, digest, acr, at, executor, correlationId);
    }

    private sealed class AdmissionMinter : PlatformAdmission.Minter
    {
        internal static PlatformAdmission Create(
            PlatformOperatorWitness witness, PlatformOperation operation, string? ticket, DateTimeOffset issuedAt) =>
            Mint(witness, operation, ticket, issuedAt);
    }
}
