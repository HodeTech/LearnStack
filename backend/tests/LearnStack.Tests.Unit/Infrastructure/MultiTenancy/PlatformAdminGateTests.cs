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
    public async Task The_Rebind_Operation_Is_Admitted_Only_For_The_Identity_Authority_Admin_Grant()
    {
        // ADR-0104 § 3: the offline tool's operator holds platform.identity_authority.admin, and no other
        // Platform grant stands in for it, the killswitch and the onboarding grants included.
        var rebind = PlatformOperations.IdentityAuthorityRebind;
        rebind.Permission.Key.Should().Be("platform.identity_authority.admin");

        var admitted = await Gate(Grants("operator-1", PlatformPermissions.IdentityAuthorityAdmin.Key))
            .AdmitAsync(TestPlatformAdmissions.Witness(), rebind, "CHG-104");
        admitted.IsAdmitted.Should().BeTrue();
        admitted.Admission!.Operation.Should().BeSameAs(rebind);
        admitted.Admission.Ticket.Should().Be("CHG-104");

        foreach (var other in PlatformPermissions.All.Where(permission => permission != PlatformPermissions.IdentityAuthorityAdmin))
        {
            (await Gate(Grants("operator-1", other.Key)).AdmitAsync(TestPlatformAdmissions.Witness(), rebind, null))
                .Refusal.Should().Be(PlatformRefusal.MissingPermission, other.Key);
        }

        // The converse: the rebind grant admits no other act.
        foreach (var operation in PlatformOperations.All.Where(operation => !ReferenceEquals(operation, rebind)))
        {
            (await Gate(Grants("operator-1", PlatformPermissions.IdentityAuthorityAdmin.Key))
                .AdmitAsync(TestPlatformAdmissions.Witness(), operation, null))
                .Refusal.Should().Be(PlatformRefusal.MissingPermission, operation.AuditSlug);
        }

        // An admission for the rebind cannot be spent on the scope or the executor, neither of which is its owner.
        rebind.NeedsPlatformRole.Should().BeFalse();
    }

    [Theory]
    [InlineData("platform.delivery.inspect", "platform.delivery.read")]
    [InlineData("platform.delivery.replay", "platform.delivery.write")]
    [InlineData("platform.delivery.reset", "platform.delivery.write")]
    public async Task A_Recovery_Operation_Is_Admitted_Only_For_Its_Own_Delivery_Permission(string slug, string permission)
    {
        // ADR-0103 Decision table: inspecting needs platform.delivery.read, and the two acts need platform.delivery.write. The pair
        // is pinned both ways: the table says which key each operation binds, and no other Platform key, the other
        // delivery key and the killswitch key included, admits it.
        var operation = PlatformOperations.All.Single(candidate => candidate.AuditSlug == slug);
        operation.Permission.Key.Should().Be(permission);

        var admitted = await Gate(Grants("operator-1", permission))
            .AdmitAsync(TestPlatformAdmissions.Witness(), operation, "CHG-103");
        admitted.IsAdmitted.Should().BeTrue();
        admitted.Admission!.Operation.Should().BeSameAs(operation);

        foreach (var other in PlatformPermissions.All.Where(candidate => candidate.Key != permission))
        {
            (await Gate(Grants("operator-1", other.Key)).AdmitAsync(TestPlatformAdmissions.Witness(), operation, null))
                .Refusal.Should().Be(PlatformRefusal.MissingPermission, other.Key);
        }
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
    public async Task A_Malformed_Ticket_Is_Refused_For_An_Operator_Who_Holds_The_Grant_And_Only_For_Them(string ticket)
    {
        // The grant is read first: an operator without it is refused as such whatever the request
        // carries, so a malformed ticket tells them nothing and is counted under the cause an alert on
        // ungranted operators watches.
        var granted = Grants("operator-1", PlatformPermissions.KillswitchWrite.Key);
        (await Gate(granted).AdmitAsync(TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, ticket))
            .Refusal.Should().Be(PlatformRefusal.InvalidTicket);
        granted.Reads.Should().Be(1);

        var ungranted = Grants("someone-else", PlatformPermissions.KillswitchWrite.Key);
        (await Gate(ungranted).AdmitAsync(TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, ticket))
            .Refusal.Should().Be(PlatformRefusal.MissingPermission);
    }

    [Fact]
    public async Task Each_Refusal_Is_Counted_Under_A_Snake_Case_Cause()
    {
        var causes = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PlatformOperatorMetrics.MeterName
                && instrument.Name == PlatformOperatorMetrics.RefusalCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "cause", Value: string cause })
                {
                    causes.Add(cause);
                }
            }
        });
        listener.Start();
        var gate = Gate(Grants("operator-1", PlatformPermissions.KillswitchWrite.Key));

        await gate.AdmitAsync(TestPlatformAdmissions.Witness("someone-else"), PlatformOperations.KillswitchToggle, null);
        await gate.AdmitAsync(TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, "has space");
        await gate.AdmitAsync(null!, PlatformOperations.KillswitchToggle, null);

        causes.Should().Contain(["missing_permission", "invalid_ticket", "invalid_witness"]);
    }

    [Fact]
    public async Task An_Unregistered_Operation_Is_Refused()
    {
        var gate = Gate(Grants("operator-1", PlatformPermissions.KillswitchWrite.Key));

        // A lookalike is not the registered instance, however identical its content.
        var lookalike = typeof(PlatformOperation).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)[0]
            .Invoke(["tenancy.killswitch.toggle", "killswitch_toggle", PlatformPermissions.KillswitchWrite, PlatformOperationExecutor.PlatformScope, null]);

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
            .Invoke(["tenancy.killswitch.toggle", "killswitch_toggle", PlatformPermissions.KillswitchWrite, PlatformOperationExecutor.PlatformScope, null]);

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
    public async Task The_Gate_Stamps_The_Admission_With_Its_Own_Clock()
    {
        var instant = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);
        var gate = Gate(Grants("operator-1", PlatformPermissions.KillswitchWrite.Key), new FixedClock(instant));

        var result = await gate.AdmitAsync(TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, null);

        result.Admission!.IssuedAt.Should().Be(instant, "the scope bounds the admission's age from this stamp");
    }

    [Fact]
    public async Task A_Stale_Admission_Never_Reaches_The_Credential_And_Is_Not_Spent()
    {
        var scope = Build(out var dataSource);
        var stale = TestPlatformAdmissions.Admission(
            issuedAt: DateTimeOffset.UtcNow - PlatformAdmission.MaximumAge - TimeSpan.FromSeconds(5));

        var act = async () => await scope.EnterAsync(stale, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stale*");
        dataSource.IsValueCreated.Should().BeFalse("the age check precedes the credential");
        stale.TryConsume().Should().BeTrue("a refused-as-stale admission was never spent");
    }

    [Fact]
    public async Task A_Future_Dated_Admission_Is_Refused_Like_A_Stale_One()
    {
        var scope = Build(out var dataSource);
        var future = TestPlatformAdmissions.Admission(
            issuedAt: DateTimeOffset.UtcNow + PlatformAdmission.MaximumAge + TimeSpan.FromHours(1));

        var act = async () => await scope.EnterAsync(future, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stale*");
        dataSource.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task An_Admission_Opens_One_Scope_Only()
    {
        // The first entry spends the admission and then fails at the absent credential; the
        // second finds it spent and says so, rather than reaching for the credential again.
        var scope = Build(out var dataSource);
        var admission = TestPlatformAdmissions.Admission();

        var first = async () => await scope.EnterAsync(admission, CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not configured*");

        var second = async () => await scope.EnterAsync(admission, CancellationToken.None);
        await second.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already used*");
        dataSource.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public void An_Admission_Is_Consumed_By_Exactly_One_Caller_Even_Concurrently()
    {
        var admission = TestPlatformAdmissions.Admission();

        var winners = Enumerable.Range(0, 64).AsParallel().WithDegreeOfParallelism(16)
            .Select(_ => admission.TryConsume()).Count(won => won);

        winners.Should().Be(1);
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
    public async Task A_Catalogue_That_Lost_Only_The_Operation_Slug_Never_Opens_A_Connection()
    {
        // The entry slug is declared, the operation's result slug is not: the scope must refuse before
        // the credential rather than enter, run the act and discover that its MUST row cannot be
        // classified (ADR-0044 § 10).
        var scope = Build(out var dataSource, new AuditCatalog([new EntryOnlyCatalogSource()]));

        var act = async () => await scope.EnterAsync(TestPlatformAdmissions.Admission(), CancellationToken.None);

        (await act.Should().ThrowAsync<AuditWriteFailedException>())
            .Which.Message.Should().Contain(PlatformOperations.KillswitchToggle.AuditSlug);
        dataSource.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public void The_Shipped_Tenancy_Source_Declares_The_Slugs_The_Scope_Records()
    {
        var catalogue = new AuditCatalog([new TenancyAuditCatalogSource()]);

        catalogue.TryGetOffPath(PlatformAdminScope.EnterOperation, out var entry).Should().BeTrue();
        entry.OperationClass.Should().Be(OperationClass.Must);
        entry.OperationType.Should().Be(OperationType.SecurityEvent);
        entry.ModuleName.Should().Be("platform", "the scope belongs to no module's request path");

        // Only the operations that enter the platform scope write their result row off-path; the
        // onboarding acts run through the pipeline, whose request-keyed catalogue the audit-coverage
        // rules join to the matrices.
        foreach (var operation in PlatformOperations.All.Where(operation => operation.NeedsPlatformRole))
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
        options.Grants.Add(new PlatformOperatorGrant { Subject = "operator-1", Permissions = { "platform.never.write" } });
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

    [Fact]
    public async Task A_Grant_Removed_From_A_Reloaded_Configuration_Is_Refused_On_The_Next_Act()
    {
        var options = new PlatformOperatorGrantOptions
        {
            Issuer = TestPlatformAdmissions.Issuer,
            Grants = { new PlatformOperatorGrant { Subject = "operator-1", Permissions = { PlatformPermissions.KillswitchWrite.Key } } },
        };
        var monitor = new MutableMonitor(options);
        var source = new ConfigurationPlatformOperatorGrantSource(monitor, NullLogger<ConfigurationPlatformOperatorGrantSource>.Instance);
        var gate = Gate(source);

        (await gate.AdmitAsync(TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, null)).IsAdmitted
            .Should().BeTrue();

        monitor.Replace(new PlatformOperatorGrantOptions { Issuer = TestPlatformAdmissions.Issuer });

        (await gate.AdmitAsync(TestPlatformAdmissions.Witness(), PlatformOperations.KillswitchToggle, null)).Refusal
            .Should().Be(PlatformRefusal.MissingPermission, "withdrawal takes effect on the next act, not at token expiry");
    }

    [Fact]
    public async Task A_Reload_That_Fails_Validation_Refuses_Every_Operator_Until_It_Is_Valid()
    {
        var monitor = new MutableMonitor(new PlatformOperatorGrantOptions
        {
            Issuer = TestPlatformAdmissions.Issuer,
            Grants = { new PlatformOperatorGrant { Subject = "operator-1", Permissions = { PlatformPermissions.KillswitchWrite.Key } } },
        });
        var source = new ConfigurationPlatformOperatorGrantSource(monitor, NullLogger<ConfigurationPlatformOperatorGrantSource>.Instance);
        (await source.IsGrantedAsync(Digest, PlatformPermissions.KillswitchWrite.Key)).Should().BeTrue();

        monitor.FailWith(new OptionsValidationException(
            nameof(PlatformOperatorGrantOptions), typeof(PlatformOperatorGrantOptions), ["a reload that is not valid"]));

        (await source.IsGrantedAsync(Digest, PlatformPermissions.KillswitchWrite.Key)).Should().BeFalse(
            "an invalid reload fails closed rather than keeping the last good grants");
    }

    private static PlatformOperatorGate Gate(IPlatformOperatorGrantSource grants, IClock? clock = null) =>
        new(grants, clock ?? new SystemClock(), NullLogger<PlatformOperatorGate>.Instance);

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

    private sealed class FixedClock(DateTimeOffset instant) : IClock
    {
        public DateTimeOffset UtcNow => instant;
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

    private sealed class MutableMonitor(PlatformOperatorGrantOptions initial) : IOptionsMonitor<PlatformOperatorGrantOptions>
    {
        private PlatformOperatorGrantOptions _value = initial;
        private Exception? _failure;

        public PlatformOperatorGrantOptions CurrentValue => _failure is null ? _value : throw _failure;
        public void Replace(PlatformOperatorGrantOptions value) => _value = value;
        public void FailWith(Exception failure) => _failure = failure;
        public PlatformOperatorGrantOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PlatformOperatorGrantOptions, string?> listener) => null;
    }

    /// <summary>Declares the scope's entry slug and nothing else, as a deployment that lost a slug would.</summary>
    private sealed class EntryOnlyCatalogSource : IAuditCatalogSource
    {
        public string ModuleName => "tenancy";

        public void Describe(IAuditCatalogBuilder builder) =>
            builder.DeclareOffPath("platform.admin_scope.enter", OperationType.SecurityEvent, OperationClass.Must);
    }

    private sealed class FixedMonitor(PlatformOperatorGrantOptions value) : IOptionsMonitor<PlatformOperatorGrantOptions>
    {
        public PlatformOperatorGrantOptions CurrentValue => value;
        public PlatformOperatorGrantOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PlatformOperatorGrantOptions, string?> listener) => null;
    }
}
