using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using LearnStack.Tests.Unit.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.MultiTenancy;

/// <summary>
/// What stands between an operator and a <c>BYPASSRLS</c> connection (ADR-0101).
/// </summary>
/// <remarks>
/// No Docker: every case here is decided before a connection is opened, which is the property
/// under test. The data source throws if forced, so a case that completes is a case where
/// nothing reached the credential.
/// </remarks>
public sealed class PlatformAdminGateTests
{
    private static readonly string Digest = PlatformOperatorSubject.Digest(TestPlatformAdmissions.Issuer, "operator-1");

    [Fact]
    public async Task An_Operator_Holding_The_Grant_Is_Admitted_For_The_Operation()
    {
        var gate = Gate(Grants("operator-1", PlatformPermissions.KillswitchWrite.Key));

        var result = await gate.AdmitAsync(
            TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, "INC-1", CancellationToken.None);

        result.IsAdmitted.Should().BeTrue();
        result.Admission!.Operation.Should().BeSameAs(PlatformOperations.KillswitchToggle);
        result.Admission.Ticket.Should().Be("INC-1");
        result.Admission.Operator.SubjectDigest.Should().Be(Digest);
    }

    [Fact]
    public async Task An_Operator_Without_The_Grant_Is_Refused_Even_With_A_Valid_Witness()
    {
        var gate = Gate(Grants("someone-else", PlatformPermissions.KillswitchWrite.Key));

        var result = await gate.AdmitAsync(
            TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, null, CancellationToken.None);

        result.IsAdmitted.Should().BeFalse();
        result.Refusal.Should().Be(PlatformRefusal.MissingPermission);
    }

    [Fact]
    public async Task A_Grant_For_Another_Permission_Does_Not_Admit()
    {
        // The grant is the permission, matched exactly: a different Platform key, or a prefix
        // of the right one, is not the key.
        var gate = Gate(Grants("operator-1", "platform.killswitch"));

        (await gate.AdmitAsync(TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, null))
            .Refusal.Should().Be(PlatformRefusal.MissingPermission);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("this-ticket-is-far-too-long-to-be-a-ticket-reference-it-exceeds-sixty-four")]
    public async Task A_Malformed_Ticket_Is_Refused_Before_The_Grant_Is_Read(string ticket)
    {
        var grants = Grants("operator-1", PlatformPermissions.KillswitchWrite.Key);
        var gate = Gate(grants);

        var result = await gate.AdmitAsync(TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, ticket);

        result.Refusal.Should().Be(PlatformRefusal.InvalidTicket);
        grants.Reads.Should().Be(0, "the ticket grammar is checked before any grant is evaluated");
    }

    [Fact]
    public async Task An_Unregistered_Operation_Is_Refused()
    {
        var gate = Gate(Grants("operator-1", PlatformPermissions.KillswitchWrite.Key));

        // A lookalike is not the registered instance, however identical its content.
        var lookalike = typeof(PlatformOperation).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0]
            .Invoke(["tenancy.killswitch.toggle", "killswitch_toggle", PlatformPermissions.KillswitchWrite, true]);

        (await gate.AdmitAsync(TestPlatformAdmissions.Witness(), (PlatformOperation)lookalike, null))
            .Refusal.Should().Be(PlatformRefusal.UnknownOperation);
    }

    [Fact]
    public async Task A_Missing_Witness_Is_Refused()
    {
        var gate = Gate(Grants("operator-1", PlatformPermissions.KillswitchWrite.Key));

        (await gate.AdmitAsync(null!, PlatformOperations.KillswitchToggle, null))
            .Refusal.Should().Be(PlatformRefusal.InvalidWitness);
    }

    [Fact]
    public async Task Entry_With_An_Unregistered_Operation_Never_Touches_The_Credential()
    {
        // The scope also refuses a registered operation that does not use the platform role,
        // but the registry holds none yet, so that second guard cannot be driven until a
        // packet registers one (P03-9 Step 2 adds the first) and is exercised there.
        var scope = Build(out var dataSource);
        var lookalike = typeof(PlatformOperation).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0]
            .Invoke(["tenancy.killswitch.toggle", "killswitch_toggle", PlatformPermissions.KillswitchWrite, true]);

        var act = async () => await scope.EnterAsync(
            TestPlatformAdmissions.Admission((PlatformOperation)lookalike), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        dataSource.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task Ordinary_job_is_refused_before_credential_resolution()
    {
        var fixture = new BackgroundJobExecutionAdmissionTests.Fixture();
        var scope = Build(out var source);
        using var admission = fixture.Enter();

        var enter = async () => await scope.EnterAsync(TestPlatformAdmissions.Admission());

        await enter.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ordinary background-job*");
        source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task A_Permitted_Caller_Reaches_The_Credential_And_Fails_There_When_It_Is_Absent()
    {
        var scope = Build(out var dataSource);

        var act = async () => await scope.EnterAsync(TestPlatformAdmissions.Admission(), CancellationToken.None);

        // The TYPE, not the message: the Lazy under test is built by this file, so an assertion
        // on its text would match the double. Production's message is covered in
        // PlatformAdminScopeTests.An_Absent_Credential_Names_The_Key_Rather_Than_Degrading.
        await act.Should().ThrowAsync<InvalidOperationException>();
        dataSource.IsValueCreated.Should().BeFalse("a Lazy whose factory threw never records a value");
    }

    [Fact]
    public async Task An_Operation_The_Catalogue_Does_Not_Declare_Never_Opens_A_Connection()
    {
        // A deployment whose catalogue lost a slug must not spend a BYPASSRLS connection
        // discovering that it cannot record the entry (ADR-0044 § 10).
        var scope = Build(out var dataSource, new AuditCatalog([]));

        var act = async () => await scope.EnterAsync(TestPlatformAdmissions.Admission(), CancellationToken.None);

        (await act.Should().ThrowAsync<AuditWriteFailedException>())
            .Which.Message.Should().Contain(PlatformAdminScope.EnterOperation);
        dataSource.IsValueCreated.Should().BeFalse("the refusal precedes the credential");
    }

    [Fact]
    public void The_Shipped_Tenancy_Source_Declares_The_Slugs_The_Scope_Records()
    {
        var catalogue = new AuditCatalog([new TenancyAuditCatalogSource()]);

        catalogue.TryGetOffPath(PlatformAdminScope.EnterOperation, out var entry).Should().BeTrue();
        entry.OperationClass.Should().Be(OperationClass.Must);
        entry.OperationType.Should().Be(OperationType.SecurityEvent);
        entry.ModuleName.Should().Be("platform", "the scope belongs to no module's request path");

        foreach (var operation in PlatformOperations.All)
        {
            catalogue.TryGetOffPath(operation.AuditSlug, out var declared)
                .Should().BeTrue($"'{operation.AuditSlug}' must be declared so its result row can be recorded");
            declared.OperationClass.Should().Be(OperationClass.Must);
        }
    }

    [Fact]
    public void The_Registry_Is_Closed_And_Every_Operation_Names_A_Registered_Platform_Permission()
    {
        PlatformOperations.All.Should().OnlyHaveUniqueItems(operation => operation.AuditSlug);
        PlatformOperations.All.Should().OnlyHaveUniqueItems(operation => operation.ReasonSlug);
        foreach (var operation in PlatformOperations.All)
        {
            PlatformPermissions.TryGet(operation.Permission.Key, out var registered).Should().BeTrue();
            registered.Should().BeSameAs(operation.Permission);
            operation.Permission.Scope.Should().Be(LearnStack.SharedKernel.Permissions.PermissionScope.Platform);
        }
    }

    [Fact]
    public void The_Grant_Validator_Refuses_Unregistered_Keys_And_The_Demo_Operator_Outside_Development()
    {
        var options = new PlatformOperatorGrantOptions { Issuer = TestPlatformAdmissions.Issuer };
        options.Grants.Add(new PlatformOperatorGrant { Subject = "operator-1", Permissions = { "platform.tenant.write" } });
        new PlatformOperatorGrantOptionsValidator(DeploymentMode.Development).Validate(null, options).Failed.Should().BeTrue(
            "a key no operation has registered is not a grant");

        var demo = new PlatformOperatorGrantOptions { Issuer = TestPlatformAdmissions.Issuer };
        demo.Grants.Add(new PlatformOperatorGrant
        {
            Subject = PlatformOperatorGrantOptions.DemoOperatorSubject,
            Permissions = { PlatformPermissions.KillswitchWrite.Key },
        });
        new PlatformOperatorGrantOptionsValidator(DeploymentMode.Development).Validate(null, demo).Succeeded.Should().BeTrue();
        new PlatformOperatorGrantOptionsValidator(DeploymentMode.SaaS).Validate(null, demo).Failed.Should().BeTrue(
            "the exported demo operator may hold a grant in Development only");
    }

    [Fact]
    public async Task The_Configuration_Source_Matches_The_Digest_And_Fails_Closed_Without_An_Issuer()
    {
        var withIssuer = Source(new PlatformOperatorGrantOptions
        {
            Issuer = TestPlatformAdmissions.Issuer,
            Grants = { new PlatformOperatorGrant { Subject = "operator-1", Permissions = { PlatformPermissions.KillswitchWrite.Key } } },
        });
        (await withIssuer.IsGrantedAsync(Digest, PlatformPermissions.KillswitchWrite.Key)).Should().BeTrue();
        (await withIssuer.IsGrantedAsync(PlatformOperatorSubject.Digest("https://other/", "operator-1"), PlatformPermissions.KillswitchWrite.Key))
            .Should().BeFalse("the same subject at another issuer is another operator");

        var noIssuer = Source(new PlatformOperatorGrantOptions
        {
            Grants = { new PlatformOperatorGrant { Subject = "operator-1", Permissions = { PlatformPermissions.KillswitchWrite.Key } } },
        });
        (await noIssuer.IsGrantedAsync(Digest, PlatformPermissions.KillswitchWrite.Key)).Should().BeFalse();
    }

    private static PlatformOperatorGate Gate(IPlatformOperatorGrantSource grants) =>
        new(grants, NullLogger<PlatformOperatorGate>.Instance);

    private static CountingGrants Grants(string subject, params string[] keys) =>
        new(TestPlatformAdmissions.Issuer, subject, keys);

    private static ConfigurationPlatformOperatorGrantSource Source(PlatformOperatorGrantOptions options) =>
        new(new FixedMonitor(options), NullLogger<ConfigurationPlatformOperatorGrantSource>.Instance);

    private static PlatformAdminScope Build(out PrivilegedDataSource dataSource, IAuditCatalog? catalog = null)
    {
        dataSource = new PrivilegedDataSource(() => throw new InvalidOperationException(
            "ConnectionStrings:PlatformAdmin is not configured in this test."));

        // The catalogue is real and built from the shipped Tenancy source, so a fake declaring
        // the slugs cannot let this file pass while the source that must declare them did not.
        return new PlatformAdminScope(
            dataSource,
            catalog ?? new AuditCatalog([new TenancyAuditCatalogSource()]),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new SystemClock(),
            new SystemGuidFactory(),
            NullLogger<PlatformAdminScope>.Instance);
    }

    private sealed class CountingGrants(string issuer, string subject, string[] keys) : IPlatformOperatorGrantSource
    {
        public int Reads { get; private set; }

        public ValueTask<bool> IsGrantedAsync(string subjectDigest, string permissionKey, CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(
                subjectDigest == PlatformOperatorSubject.Digest(issuer, subject) && keys.Contains(permissionKey));
        }
    }

    private sealed class FixedMonitor(PlatformOperatorGrantOptions value) : IOptionsMonitor<PlatformOperatorGrantOptions>
    {
        public PlatformOperatorGrantOptions CurrentValue => value;
        public PlatformOperatorGrantOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PlatformOperatorGrantOptions, string?> listener) => null;
    }
}
