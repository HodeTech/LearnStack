using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Caching;
using LearnStack.Modules.Tenancy.Infrastructure;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobExternalIsolationTests
{
    [Theory]
    [InlineData("audit_config")]
    [InlineData("standalone_audit")]
    [InlineData("tenant_flags")]
    [InlineData("killswitch")]
    public async Task Concrete_cold_helpers_refuse_source_less_live_and_retained_external_flows_before_lazy_source_or_physical_SQL(string helper)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = metrics.GetRequiredService<IMeterFactory>();
        var opened = 0;
        await using var source = new NpgsqlDataSourceBuilder(database.AppConnectionString)
            .UsePhysicalConnectionInitializer(_ => Interlocked.Increment(ref opened), _ =>
            {
                Interlocked.Increment(ref opened);
                return Task.CompletedTask;
            }).Build();
        var created = 0;
        var lazy = new Lazy<NpgsqlDataSource>(() => { Interlocked.Increment(ref created); return source; });
        var cache = new InMemoryCacheService(new SystemClock(), meterFactory);
        var context = new AnnouncedContext();
        var classifier = new AuditConfigService(cache, lazy, NullLogger<AuditConfigService>.Instance,
            new StaticTenantContextAccessor(context));
        var overlay = new KillswitchOverlay(lazy, cache, NullLogger<KillswitchOverlay>.Instance);
        var flags = new FeatureFlags(context, new NullEntitlementProvider(), overlay, lazy, cache, NullLogger<FeatureFlags>.Instance);
        var store = new PostgresAuditStore(new AuditStateCapture(), lazy, NullLogger<PostgresAuditStore>.Instance,
            meterFactory, new AuditHealth(), new AuditCatalog([]), new StaticTenantContextAccessor(context), new LearnStack.Infrastructure.Persistence.GlobalIdentityEnrollment());
        var draft = HelperDraft();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task inherited;
        using (BackgroundJobExecutionAdmission.EnterExternal())
        {
            JobApplicationSourceOwner.Current.Should().BeNull("the owner-null branch is the reported bypass");
            await RefusedAsync();
            inherited = Task.Run(async () => { await release.Task; await RefusedAsync(); });
        }
        release.SetResult();
        await inherited.WaitAsync(TimeSpan.FromSeconds(5));
        created.Should().Be(0);
        opened.Should().Be(0, "both denials must precede source resolution, so no helper SQL can have started");
        await InvokeAsync();
        created.Should().Be(1);
        opened.Should().BeGreaterThan(0, "the same real public helper must reach its app-role database outside the marker");
        if (helper == "standalone_audit")
        {
            await using var app = await source.OpenConnectionAsync();
            await using var transaction = await app.BeginTransactionAsync();
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await using var query = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE id=@id", app, transaction);
            query.Parameters.AddWithValue("id", draft.Id.Value);
            ((long)(await query.ExecuteScalarAsync())!).Should().Be(1);
        }

        async Task RefusedAsync()
        {
            Func<Task> invoke = InvokeAsync;
            (await invoke.Should().ThrowAsync<InvalidOperationException>()).WithMessage(
                "The job application-source owner refuses another acquisition or activation.");
        }

        async Task InvokeAsync()
        {
            switch (helper)
            {
                case "audit_config":
                    (await classifier.ClassifyAsync(context.TenantId,
                        new AuditCatalogEntry("tenancy", "tenancy.external-isolation.proof", OperationType.Create,
                            OperationClass.Should, typeof(object)))).Should().Be(AuditClassification.Should);
                    break;
                case "standalone_audit": await store.WriteStandaloneAsync(draft); break;
                case "tenant_flags": (await flags.IsEnabledAsync(FeatureKeys.LessonPlayerV2)).Should().BeFalse(); break;
                case "killswitch": (await overlay.IsEnabledAsync(KillswitchKeys.EmailDispatchEnabled)).Should().BeTrue(); break;
                default: throw new ArgumentException("The fixture names one concrete helper.", nameof(helper));
            }
        }
    }

    private static AuditEntryDraft HelperDraft() => new()
    {
        Id = AuditEntryId.From(Guid.CreateVersion7()),
        TenantId = TenantId.From(SchemaFixture.TenantA),
        OrganizationId = null,
        ActorUserId = UserId.SystemActor,
        ActorEmail = null,
        ModuleName = "tenancy",
        Operation = "tenancy.external-isolation.proof",
        OperationType = OperationType.Create,
        OperationClass = OperationClass.Must,
        EntityType = null,
        EntityId = null,
        Outcome = AuditOutcome.Success,
        ErrorKey = null,
        Reason = null,
        BeforeState = null,
        AfterState = null,
        Changes = null,
        CorrelationId = null,
        IpAddress = null,
        UserAgent = null,
        Timestamp = DateTimeOffset.UtcNow,
        Metadata = null,
    };
}
