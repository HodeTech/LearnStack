using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AuditRedactionCanaryTests
{
    [Fact]
    public async Task Real_captures_are_redacted_at_every_path_shape()
    {
        // The generated canaries above are built in the capture's shape; this is that shape as the capture writes it, for
        // each form of the grammar: a property (a request's subject and requester), an array of identifiers (its frozen
        // Memberships) and a contained collection's member (a tenant's feature flags, keyed by flag).
        var tenant = Guid.CreateVersion7();
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var request = DataSubjectRequest.FileForSubject(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Erasure,
            UserId.From(OtherPerson), UserId.From(Subject), new DataRequestScopeGroup(TenantId.From(tenant), null),
            [MembershipId.From(Membership1), MembershipId.From(Membership2)], clock.UtcNow).Value!;
        var aggregate = Tenant.Create(TenantId.From(tenant), "canary-" + tenant.ToString("N")[..12], "Canary", clock, UserId.From(OtherPerson));
        aggregate.SetFeatureFlag(FeatureKeys.LessonPlayerV2, "true", clock, UserId.From(Subject));
        aggregate.SetFeatureFlag(FeatureKeys.AiPronunciationFeedback, "false", clock, UserId.From(OtherPerson));

        var captured = new[] { await CaptureAsync(IdentityModel(), request), await CaptureAsync(TenancyModel(), aggregate) };
        var rows = captured.Select(change => new CanaryRow("captured " + change.EntityType, tenant, null, OtherPerson, change.EntityType,
            change.EntityId!, Parse(change.BeforeJson), Parse(change.AfterJson), Parse(Diff(change.Fields)), null, Selected: true,
            null, null, null)).ToList();

        await using var scope = await OpenAsync(schema.Postgres.PlatformConnectionString);
        await InsertAsync(scope, rows);
        var report = await RedactAsync(scope, Plan(tenant, DataRequestOrigin.Self, organization: null));
        var after = await ReadAsync(scope, rows);

        report.RowsSelected.Should().Be(2);
        var sentinel = SensitiveTokenCatalog.RedactedValue;
        var dsr = after[rows[0].Id];
        var state = Parse(dsr.AfterText)!.AsObject();
        state["SubjectUserId"]!.GetValue<string>().Should().Be(sentinel);
        state["RequesterUserId"]!.GetValue<string>().Should().Be(Text(OtherPerson), "the administrator who filed it is another person");
        state["CreatedBy"]!.GetValue<string>().Should().Be(Text(OtherPerson));
        state["MembershipSnapshotJson"]!.AsArray().Select(element => element!.GetValue<string>()).Should().Equal(sentinel, sentinel);
        dsr.ChangesText.Should().NotContain(Text(Subject)).And.NotContain(Text(Membership1)).And.NotContain(Text(Membership2))
            .And.Contain(Text(OtherPerson));

        var tenantRow = after[rows[1].Id];
        var flags = Parse(tenantRow.AfterText)!["FeatureFlags"]!.AsObject();
        flags[FeatureKeys.LessonPlayerV2.Value]!["UpdatedBy"]!.GetValue<string>().Should().Be(sentinel);
        flags[FeatureKeys.AiPronunciationFeedback.Value]!["UpdatedBy"]!.GetValue<string>().Should().Be(Text(OtherPerson));
        tenantRow.ChangesText.Should().NotContain(Text(Subject)).And.Contain(
            "/Tenant/" + tenant.ToString("D") + "/FeatureFlags/" + FeatureKeys.LessonPlayerV2.Value + "/UpdatedBy");
        tenantRow.AfterText.Should().NotContain(Text(Subject));
        await scope.Transaction.RollbackAsync();
    }

    /// <summary>Runs the real capture over one new aggregate on a model-only context, as a save would.</summary>
    private async Task<CapturedEntityChange> CaptureAsync(DbContext context, object aggregate)
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var capture = new AuditStateCapture();
        await using (context)
        {
            context.Add(aggregate);
            new AuditChangeTrackerInterceptor(capture, unit).Capture(context);
        }

        return capture.Changes.Should().ContainSingle(change => change.EntityType == aggregate.GetType().Name).Subject;
    }

    /// <summary>The <c>changes</c> column the composer writes from one capture's fields (<c>AuditDraftComposer</c>).</summary>
    private static string? Diff(IReadOnlyList<CapturedFieldChange> fields) =>
        fields.Count == 0
            ? null
            : "[" + string.Join(',', fields.Select(field => "{\"path\":" + AuditJson.Quote(field.Path) + ",\"before\":"
                + (field.BeforeJson ?? AuditJson.Null) + ",\"after\":" + (field.AfterJson ?? AuditJson.Null) + "}")) + "]";

    private static JsonNode? Parse(string? json) => json is null ? null : JsonNode.Parse(json);

    private static IdentityDbContext IdentityModel() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql("Host=model-only;Database=model-only;Username=model-only").Options,
            StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);

    private static TenancyDbContext TenancyModel() =>
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql("Host=model-only;Database=model-only;Username=model-only").Options,
            StaticTenantContextAccessor.Unresolved);
}
