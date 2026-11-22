using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Customization.Infrastructure.DataRequests;
using LearnStack.Modules.Education.Infrastructure.DataRequests;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.Modules.Tenancy.Infrastructure.DataRequests;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Audit;

/// <summary>
/// ADR-0108 §§ 1–2: the executor's refusals, each before any statement, and the API composition's one registration of it
/// over every module's locator. The statements themselves are proved against PostgreSQL by the integration canaries.
/// </summary>
public sealed class AuditRedactionExecutorTests
{
    private static readonly AuditRedactionExecutor Executor = new([new IdentityUserReferenceLocator(), new TenancyUserReferenceLocator(),
        new CustomizationUserReferenceLocator(), new EducationUserReferenceLocator()]);

    private static readonly AuditRedactionPlan Erasure = Plan(DataRequestKind.Erasure);

    private static readonly AuditRedactionPlan ClosureTenant = AuditRedactionPlan.ForClosureTenant(
        TenantId.From(Guid.CreateVersion7()), UserId.From(Guid.CreateVersion7()), [Guid.CreateVersion7(), Guid.CreateVersion7()]);

    private static readonly AuditRedactionPlan ClosureSentinel = AuditRedactionPlan.ForClosureSentinel(
        UserId.From(Guid.CreateVersion7()), IdentitySubjectKey.FromBytes(new byte[32]));

    private static readonly Type[] Locators =
    [
        typeof(IdentityUserReferenceLocator), typeof(TenancyUserReferenceLocator),
        typeof(CustomizationUserReferenceLocator), typeof(EducationUserReferenceLocator),
    ];

    [Fact]
    public async Task Only_the_sanctioned_operation_over_an_erasure_plan_is_admitted()
    {
        foreach (var operation in PlatformOperations.All.Where(operation => !ReferenceEquals(operation, PlatformOperations.ErasureComplete)))
        {
            await Refused(Erasure, Spent(operation), new StatementlessConnection(), SanctionRefusal, operation.AuditSlug);
        }

        // The closure's Finalize is the other redacting operation, and still not this plan's.
        await Refused(Erasure, Spent(PlatformOperations.ClosureFinalize), new StatementlessConnection(), SanctionRefusal,
            "an erasure's plan is redacted only by its own privileged step, never by a closure's Finalize");

        // The pairing admitted, the next check refuses: the sanction let it through.
        await Refused(Erasure, Spent(PlatformOperations.ErasureComplete), new StatementlessConnection(), ConnectionRefusal,
            "complete_erasure redacts an erasure's plan");
    }

    [Fact]
    public async Task Only_the_closure_s_finalize_over_a_closure_pass_is_admitted()
    {
        // ADR-0107 § 7, ADR-0108 § 4: a closure's tenant and sentinel passes are the Finalize's alone; complete_erasure,
        // the other redacting operation, is refused them like every other.
        foreach (var (name, plan) in new[] { ("tenant pass", ClosureTenant), ("sentinel pass", ClosureSentinel) })
        {
            foreach (var operation in PlatformOperations.All.Where(operation => !ReferenceEquals(operation, PlatformOperations.ClosureFinalize)))
            {
                await Refused(plan, Spent(operation), new StatementlessConnection(), SanctionRefusal, name + " under " + operation.AuditSlug);
            }

            await Refused(plan, Spent(PlatformOperations.ClosureFinalize), new StatementlessConnection(), ConnectionRefusal,
                "the closure's Finalize redacts its " + name);
        }
    }

    [Fact]
    public async Task An_admission_no_scope_spent_is_refused_and_spent()
    {
        var admission = TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete);

        await Refused(Erasure, admission, new StatementlessConnection(), "*platform scope its admission opened*",
            "the scope spends the admission it was opened with");

        admission.TryConsume().Should().BeFalse("a refused unspent admission cannot open a scope afterwards");
    }

    [Fact]
    public async Task A_connection_that_is_not_the_scopes_open_platform_connection_is_refused()
    {
        await Refused(Erasure, Spent(PlatformOperations.ErasureComplete), new StatementlessConnection(),
            ConnectionRefusal, "the executor runs on the platform scope's Npgsql connection");
    }

    [Fact]
    public async Task An_external_job_frame_is_refused()
    {
        using (JobApplicationSourceOwner.EnterExternal())
        {
            await Refused(Erasure, Spent(PlatformOperations.ErasureComplete), new StatementlessConnection(),
                "*refuses another acquisition or activation*", "an operator act never runs in an external job frame");
        }
    }

    [Fact]
    public void An_export_or_a_closure_request_is_never_a_request_redaction_plan()
    {
        FluentActions.Invoking(() => Plan(DataRequestKind.Export)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Plan(DataRequestKind.Closure)).Should().Throw<ArgumentException>(
            "a closure's redaction is its Finalize's tenant and sentinel passes, never its one request's");
    }

    [Fact]
    public void The_executor_refuses_to_exist_without_a_declaration()
    {
        FluentActions.Invoking(() => new AuditRedactionExecutor([])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task The_api_composition_registers_one_executor_over_every_module_locator()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(InvitationEmailFixtureConfiguration.Values);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OutboxDispatcher"] = "Host=localhost;Port=1;Database=learnstack;Username=learnstack_outbox_admin;Maximum Pool Size=54",
        });
        builder.AddLearnStackCrossCuttingFoundation(DeploymentMode.Development,
            typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly,
            typeof(LearnStack.Modules.Education.Application.AssemblyMarker).Assembly);
        builder.Services.AddLearnStackPersistence(builder.Configuration);

        builder.Services.Where(descriptor => descriptor.ServiceType == typeof(IAuditRedactor)).Should().ContainSingle()
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        await using var services = builder.Services.BuildServiceProvider();
        services.GetRequiredService<IAuditRedactor>().Should().BeOfType<AuditRedactionExecutor>();
        services.GetServices<IUserReferenceLocator>().Select(locator => locator.GetType()).Should().BeEquivalentTo(Locators);
    }

    /// <summary>The refusal of an act that names an operation, or a plan, the sanctioned list does not pair.</summary>
    private const string SanctionRefusal = "*does not name an operation that may redact*";

    /// <summary>The refusal of the check after the sanction: a pairing that reaches it was admitted.</summary>
    private const string ConnectionRefusal = "*open connection and its work transaction*";

    /// <summary>Expects the named refusal, so a test cannot pass on a later check that also refuses.</summary>
    /// <remarks>
    /// This proves which check refuses, not that no statement ran: the executor builds its <c>NpgsqlCommand</c>s itself and
    /// would never ask this double for one. That nothing runs before a refusal is proved against PostgreSQL by
    /// <c>AuditRedactionCanaryTests.A_refused_admission_or_plan_runs_no_statement</c> (the transaction's JIT setting and
    /// rows unchanged, and the same transaction still usable).
    /// </remarks>
    private static async Task Refused(AuditRedactionPlan plan, PlatformAdmission admission, StatementlessConnection connection,
        string message, string because) =>
        await FluentActions.Awaiting(() => Executor.RedactAsync(plan, admission, connection, connection.Transaction))
            .Should().ThrowAsync<InvalidOperationException>(because).WithMessage(message);

    private static PlatformAdmission Spent(PlatformOperation operation)
    {
        var admission = TestPlatformAdmissions.Admission(operation);
        admission.TryConsume().Should().BeTrue();
        return admission;
    }

    private static AuditRedactionPlan Plan(DataRequestKind kind) =>
        new(new UserDataSubject(Guid.CreateVersion7(), kind, DataRequestOrigin.Self, UserId.From(Guid.CreateVersion7()),
            new DataRequestScopeGroup(TenantId.From(Guid.CreateVersion7()), null), [Guid.CreateVersion7()]));

    /// <summary>An open connection that is not the platform scope's: it runs nothing.</summary>
    [SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "The transaction owns nothing.")]
    private sealed class StatementlessConnection : DbConnection
    {
        public StatementlessConnection() => Transaction = new StatementlessTransaction(this);

        public DbTransaction Transaction { get; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "statementless";

        public override string DataSource => "statementless";

        public override string ServerVersion => "18";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException("A refusal runs no statement.");
    }

    private sealed class StatementlessTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection DbConnection => connection;

        public override void Commit() => throw new NotSupportedException();

        public override void Rollback() => throw new NotSupportedException();
    }
}
