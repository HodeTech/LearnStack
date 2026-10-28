using System.Net;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Diagnostics.Metrics;

namespace LearnStack.Tests.Integration.Database;

internal static class AuthorizationEvidenceFixture
{
    internal static AuthorizationEvidenceOccurrence Occurrence(AuditEntryDraft? draft = null,
        AuthorizationEvidenceKind kind = AuthorizationEvidenceKind.Denial) => Source.Mint(kind, draft ?? Draft(kind));

    internal static AuditEntryDraft Draft(AuthorizationEvidenceKind kind = AuthorizationEvidenceKind.Denial) => new()
    {
        Id = AuditEntryId.From(Guid.CreateVersion7()),
        TenantId = TenantId.From(SchemaFixture.TenantA),
        OrganizationId = OrganizationId.From(SchemaFixture.OrgA1),
        ActorUserId = UserId.From(IdentityMembershipFixture.AdminA),
        ActorEmail = null,
        ModuleName = kind == AuthorizationEvidenceKind.Denial ? "identity" : "tenancy",
        Operation = kind == AuthorizationEvidenceKind.Denial ? "identity.authorization.deny" : "tenancy.assertion.reject_claim_context",
        OperationType = OperationType.SecurityEvent,
        OperationClass = OperationClass.Must,
        EntityType = null,
        EntityId = null,
        Outcome = AuditOutcome.Denied,
        ErrorKey = "authorization.denied",
        Reason = null,
        BeforeState = null,
        AfterState = null,
        Changes = null,
        CorrelationId = "00-12345678901234567890123456789012-1234567890123456-01",
        IpAddress = IPAddress.Parse("192.0.2.1"),
        UserAgent = "bounded-evidence-test",
        Timestamp = new DateTimeOffset(2026, 10, 27, 12, 13, 14, TimeSpan.FromHours(3)).AddTicks(1234567),
        Metadata = """{"authorization":{"version":1,"policy":"Tenant:identity.membership.read"}}""",
    };

    internal static async Task<AuthorizationEvidenceSource> SourceAsync(SchemaFixture schema, int capacity = 2,
        ILoggerFactory? logs = null)
    {
        var source = new AuthorizationEvidenceSource(schema.Postgres.AppConnectionString, capacity, logs);
        await source.QualifyAsync(IdentityMembershipFixture.Authority.AuthorityId, IdentityMembershipFixture.Authority.Issuer);
        return source;
    }

    internal static PostgresAuditStore Store(NpgsqlDataSource business, IMeterFactory meters,
        IAuthorizationEvidenceSessionFactory? evidence = null, AuditHealth? health = null,
        ILogger<PostgresAuditStore>? logger = null) => new(new AuditStateCapture(), new Lazy<NpgsqlDataSource>(() => business),
        logger ?? NullLogger<PostgresAuditStore>.Instance, meters, health ?? new AuditHealth(),
        new AuditCatalog([new CatalogSource()]), StaticTenantContextAccessor.Unresolved, new GlobalIdentityEnrollment(), evidence);

    internal static async Task DeleteAsync(SchemaFixture schema, params AuditEntryId[] ids)
    {
        await using var connection = await PostgresFixture.OpenAsync(schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand("DELETE FROM public.audit_log WHERE id = ANY(@ids)", (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("ids", ids.Select(id => id.Value).ToArray());
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Source : AuthorizationEvidenceOccurrence.MintingSource
    {
        internal static AuthorizationEvidenceOccurrence Mint(AuthorizationEvidenceKind kind, AuditEntryDraft draft) => ForOccurrence(kind, draft);
    }

    private sealed class CatalogSource : IAuditCatalogSource
    {
        public string ModuleName => "identity";
        public void Describe(IAuditCatalogBuilder builder)
        {
            builder.DeclareOffPath("identity.authorization.deny", OperationType.SecurityEvent, OperationClass.Must);
            builder.DeclareOffPath("tenancy.assertion.reject_claim_context", OperationType.SecurityEvent, OperationClass.Must);
        }
    }

    internal sealed class Meters : IMeterFactory
    {
        internal Meter Meter { get; } = new(PostgresAuditStore.MeterName);
        public Meter Create(MeterOptions options) => Meter;
        public void Dispose() => Meter.Dispose();
    }
}
