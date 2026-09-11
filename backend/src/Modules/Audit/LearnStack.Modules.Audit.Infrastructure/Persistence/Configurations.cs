using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.Modules.Audit.Domain;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Audit.Infrastructure.Persistence;

/// <summary>
/// Maps the append-only log.
/// </summary>
/// <remarks>
/// The mapping exists for the model — the query filter, the isolation sweep and the
/// migration — and for the Phase 03 read API. It is not the write path: rows arrive as
/// <c>PostgresAuditStore</c>'s parameterised <c>INSERT</c>, so every column here is
/// described rather than populated by change tracking.
/// </remarks>
internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Three closed-set text columns, three CHECKs, and the value lists differ in
        // case for a reason rather than by accident. `outcome`'s four values are
        // written lowercase by ADR-0033 § 3 and ADR-0044 § 5, both Accepted; the other
        // two store the C# enum member name unchanged, on the ck_tenants_status
        // precedent, which is what lets the admin API's ?operationType=SecurityEvent
        // filter be the same string on both sides. The asymmetry is stated here
        // because this is the one file where both renderings are visible at once.
        builder.ToTable("audit_log", table =>
        {
            table.HasCheckConstraint(
                "ck_audit_log_outcome",
                "outcome IN ('success', 'denied', 'failed', 'indeterminate')");

            table.HasCheckConstraint(
                "ck_audit_log_operation_type",
                "operation_type IN ('Create', 'Update', 'Delete', 'ReadSensitive', "
                    + "'SecurityEvent', 'PlatformAdmin', 'Action')");

            table.HasCheckConstraint(
                "ck_audit_log_operation_class",
                "operation_class IN ('Must', 'Should', 'May')");
        });

        // COMPOSITE, and not for a present reason. A partitioned table must include
        // every partition-key column in its primary key, so declaring (id, timestamp)
        // now is what lets the Phase 11 conversion be a data operation rather than a key
        // migration. It also makes the commit-in-doubt pair legal: two rows share an id
        // exactly when a COMMIT's outcome was unknown, and they differ here because the
        // standalone re-write takes a fresh clock reading.
        builder.HasKey(x => new { x.Id, x.Timestamp }).HasName("audit_log_pkey");

        // Minted app-side at pipeline step 3 — the one high-volume append-only table
        // whose id is, because the in-transaction row and any standalone replacement
        // must carry one identity and a server default cannot give two inserts the same
        // one (ADR-0023 Amendment 9).
        builder.Property(x => x.Id)
            .HasConversion<AuditEntryId.EfCoreValueConverter, AuditEntryId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        // Supplied by the store from IClock, never by the column's default. The DEFAULT
        // stays in the migration as a backstop for a row inserted by something else.
        builder.Property(x => x.Timestamp).ValueGeneratedNever().IsRequired();

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        builder.Property(x => x.OrganizationId)
            .HasConversion<OrganizationId.EfCoreValueConverter, OrganizationId.EfCoreValueComparer>();

        builder.Property(x => x.ActorUserId)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();

        builder.Property(x => x.ActorEmail).HasMaxLength(320);

        // `module`, not `module_name`. The column is the first half of the catalogue
        // key `(module, operation)` that ADR-0044 § 6 fixes and the name every DDL and
        // query in the corpus uses; the CLR property differs only because CA1716 flags
        // `Module` as a name that collides with a Visual Basic keyword, and an analyzer
        // rule about C# identifiers is not a reason to rename a database column.
        builder.Property(x => x.ModuleName)
            .HasColumnName("module")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Operation).HasMaxLength(150).IsRequired();

        builder.Property(x => x.OperationType).HasEnumAsText().IsRequired();
        builder.Property(x => x.OperationClass).HasEnumAsText().IsRequired();

        // Lowercase, and therefore not HasEnumAsText(): two Accepted ADRs write the
        // stored values as `success | denied | failed | indeterminate`.
        //
        // ignoreCase is REQUIRED, not a convenience. The stored form is lowercase and the
        // member names are PascalCase, so Enum.Parse without it throws on EVERY row —
        // including the first row the Phase 03 read API materialises. It buys nothing on
        // the tolerance side that it might look like it buys: ck_audit_log_outcome admits
        // the four lowercase literals and nothing else, and a CHECK binds every role
        // including the owner, so there is no such thing as a row stored in another case
        // for a lenient parse to rescue.
        builder.Property(x => x.Outcome)
            .HasConversion(
                value => value.ToString().ToLowerInvariant(),
                text => Enum.Parse<AuditOutcome>(text, ignoreCase: true))
            .HasColumnType("text")
            .IsRequired();

        builder.Property(x => x.EntityType).HasMaxLength(200);
        // Unbounded: an audited aggregate's key is whatever its own table admits, and a
        // bound here is a second, narrower one. At 100 characters a valid 101-character
        // host mapping — PlatformHostMapping's key admits 253 — failed its MUST row with
        // 22001, rolled the mapping back, and failed the standalone record of the attempt
        // for the same reason (measured by the fourth review of Packet 9). Truncating would
        // change the subject's identity, so the column holds the key whole.
        builder.Property(x => x.EntityId).HasColumnType("text");
        builder.Property(x => x.ErrorKey).HasMaxLength(150);
        builder.Property(x => x.Reason).HasMaxLength(500);

        // jsonb, not text: PostgreSQL refuses a malformed document at the boundary, and
        // the Phase 03 redaction path issues column-restricted UPDATEs that read inside
        // these three.
        builder.Property(x => x.BeforeState).HasColumnType("jsonb");
        builder.Property(x => x.AfterState).HasColumnType("jsonb");
        builder.Property(x => x.Changes).HasColumnType("jsonb");
        builder.Property(x => x.Metadata).HasColumnType("jsonb");

        builder.Property(x => x.CorrelationId).HasMaxLength(100);

        // inet, which Npgsql maps IPAddress onto with no configuration. A string would
        // map to text, and PostgreSQL will not assign text to inet.
        builder.Property(x => x.IpAddress).HasColumnType("inet");

        builder.Property(x => x.UserAgent).HasMaxLength(512);

        // The tenant's own view, newest first — the shape every read in the admin API
        // takes, and the one the retention purge scans.
        builder.HasIndex(x => new { x.TenantId, x.Timestamp })
            .HasDatabaseName("ix_audit_log_tenant_id_timestamp")
            .IsDescending(false, true);

        // Partial: the probe-detection queries filter by actor, and a row with no
        // principal has nothing to answer them with.
        builder.HasIndex(x => new { x.ActorUserId, x.Timestamp })
            .HasDatabaseName("ix_audit_log_actor_user_id_timestamp")
            .IsDescending(false, true)
            .HasFilter("actor_user_id IS NOT NULL");

        builder.HasIndex(x => x.CorrelationId)
            .HasDatabaseName("ix_audit_log_correlation_id")
            .HasFilter("correlation_id IS NOT NULL");

        builder.HasIndex(x => new { x.ModuleName, x.Operation, x.Timestamp })
            .HasDatabaseName("ix_audit_log_module_operation_timestamp")
            .IsDescending(false, false, true);
    }
}

/// <summary>
/// Maps the per-tenant classification overrides.
/// </summary>
internal sealed class AuditConfigConfiguration : IEntityTypeConfiguration<AuditConfig>
{
    public void Configure(EntityTypeBuilder<AuditConfig> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_config");
        builder.HasKey(x => x.Id).HasName("pk_audit_config");

        builder.Property(x => x.Id)
            .HasConversion<AuditConfigId.EfCoreValueConverter, AuditConfigId.EfCoreValueComparer>()
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasConversion<TenantId.EfCoreValueConverter, TenantId.EfCoreValueComparer>()
            .IsRequired();

        // `module`, for the reason AuditEntryConfiguration gives above: the key this
        // table overrides is `(module, operation)`.
        builder.Property(x => x.ModuleName)
            .HasColumnName("module")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Operation).HasMaxLength(150).IsRequired();
        builder.Property(x => x.IsEnabled).IsRequired();

        builder.MapAuditColumns<AuditConfig, AuditConfigId>();

        // One LIVE override per tenant per operation. Without it a second row for the same
        // slug makes the cached projection's answer depend on which one it read. Partial on
        // `deleted_at IS NULL`, for the reason ux_tenant_domains_host is: IsEnabled has no
        // setter, so changing an override is a soft delete and a fresh Declare — and an
        // index that counted the deleted row would refuse the second with 23505, holding the
        // slug against its own tenant forever (the fifth review of Packet 9).
        builder.HasIndex(x => new { x.TenantId, x.ModuleName, x.Operation })
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_audit_config_tenant_id_module_operation");
    }
}
