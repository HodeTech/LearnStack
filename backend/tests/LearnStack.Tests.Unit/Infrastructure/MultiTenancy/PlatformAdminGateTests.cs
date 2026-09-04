using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.MultiTenancy;

/// <summary>
/// What stands between a caller and a <c>BYPASSRLS</c> connection.
/// </summary>
/// <remarks>
/// No Docker: every case here is refused before a connection is opened, which is the
/// property under test. The <c>Lazy</c> throws if forced, so a case that completes at all
/// is a case where nothing reached the credential.
/// </remarks>
public sealed class PlatformAdminGateTests
{
    [Fact]
    public async Task The_Registered_Gate_Permits_Nobody()
    {
        // Nothing else instantiates this type, so without this the only gate behaviour
        // the corpus exhibits is the permissive double the Docker suite uses. Its own doc
        // says it exists so nobody makes the default permissive to unblock a demo; this
        // is the line that would notice.
        var gate = new DenyAllPlatformAdminGate();

        (await gate.IsPermittedAsync("anything", CancellationToken.None)).Should().BeFalse();
        (await gate.IsPermittedAsync("gdpr-redaction:some-user", CancellationToken.None))
            .Should().BeFalse("a plausible reason is not a permission");
    }

    [Fact]
    public async Task Entry_Is_Refused_Before_The_Credential_Is_Touched()
    {
        // ADR-0036 asks for the permission to be "checked before the scope opens". A
        // check after the connection exists would already have spent a BYPASSRLS
        // connection on a caller who was never allowed one — and, in a deployment with no
        // platform credential at all, would report the wrong failure entirely.
        var scope = Build(new DenyAllPlatformAdminGate(), out var dataSource);

        var act = async () => await scope.EnterAsync("test:denied", CancellationToken.None);

        await act.Should().ThrowAsync<PlatformAdminScopeDeniedException>();
        dataSource.IsValueCreated.Should().BeFalse("the gate runs before the credential");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_Blank_Reason_Is_Refused_Before_Anything_Else(string? reason)
    {
        // The reason is the value Packet 9 writes durably, and a blank one makes the
        // record of a cross-tenant access say nothing about why it happened. Refused
        // ahead of the gate so the failure names the caller's own mistake rather than a
        // permission they may well hold.
        var scope = Build(new PermissiveGate(), out var dataSource);

        var act = async () => await scope.EnterAsync(reason!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        dataSource.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task A_Permitted_Caller_Reaches_The_Credential_And_Fails_There_When_It_Is_Absent()
    {
        // The other side of the ordering: with the gate open, the next thing that can go
        // wrong is the credential, and it must surface as the credential rather than as
        // anything else.
        var scope = Build(new PermissiveGate(), out var dataSource);

        var act = async () => await scope.EnterAsync("test:permitted", CancellationToken.None);

        // InvalidOperationException and not PlatformAdminScopeDeniedException — the two
        // are unrelated types, so this assertion already distinguishes "refused entry"
        // from "entry allowed, credential missing".
        // The exception's TYPE is the evidence, not IsValueCreated: when a Lazy factory
        // throws, Lazy<T> caches the failure and leaves IsValueCreated false — measured.
        // So "reached the credential" is shown by the failure being the credential's,
        // which is exactly what distinguishes it from the refusal above.
        // The TYPE, not the message. The Lazy under test is built by this file, so an
        // assertion on its text would be matching the double rather than production
        // code; the production message is covered where it is produced, in
        // PlatformAdminScopeTests.An_Absent_Credential_Names_The_Key_Rather_Than_Degrading.
        await act.Should().ThrowAsync<InvalidOperationException>();
        dataSource.IsValueCreated.Should().BeFalse(
            "a Lazy whose factory threw never records a created value");
    }

    private static PlatformAdminScope Build(
        IPlatformAdminGate gate, out Lazy<NpgsqlDataSource> dataSource)
    {
        dataSource = new Lazy<NpgsqlDataSource>(() => throw new InvalidOperationException(
            "ConnectionStrings:PlatformAdmin is not configured in this test."));

        return new PlatformAdminScope(gate, dataSource, NullLogger<PlatformAdminScope>.Instance);
    }

    private sealed class PermissiveGate : IPlatformAdminGate
    {
        public ValueTask<bool> IsPermittedAsync(
            string reason, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }
}
