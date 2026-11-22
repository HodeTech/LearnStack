using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>Stores a closed vocabulary as its ADR spelling, never as a member name or a number.</summary>
internal sealed class ClosedCodeConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public ClosedCodeConverter(ClosedCodeTable<TEnum> table)
        : base(value => table.ToCode(value), code => table.Parse(code))
    {
    }
}

/// <summary>
/// <c>data_subject_requests</c> (ADR-0106 § 8, ADR-0107 § 8). The CHECKs bound every closed column and
/// both documents; <see cref="DataRequestStorageDdl"/> adds the row policy, the grants and the guard.
/// </summary>
internal sealed class DataSubjectRequestConfiguration : IEntityTypeConfiguration<DataSubjectRequest>
{
    internal const string Table = "data_subject_requests";

    /// <summary>The non-terminal statuses, the predicate of the one-active-slot index (ADR-0106 § 7).</summary>
    internal const string ActiveFilter = "status IN ('requested','scheduled','fulfilling','awaiting_privileged_step')";

    public void Configure(EntityTypeBuilder<DataSubjectRequest> builder)
    {
        builder.ToTable(Table, table =>
        {
            table.HasCheckConstraint("ck_data_subject_requests_identifiers",
                "(uuid_extract_version(id)=7 AND uuid_extract_version(subject_user_id)=7 AND subject_user_id <> '00000000-0000-7000-8000-000000000001'::uuid AND (requester_user_id IS NULL OR (uuid_extract_version(requester_user_id)=7 AND requester_user_id <> '00000000-0000-7000-8000-000000000001'::uuid))) IS TRUE");
            table.HasCheckConstraint("ck_data_subject_requests_tenant",
                "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
            table.HasCheckConstraint("ck_data_subject_requests_kind", "kind IN ('export','erasure','closure')");
            table.HasCheckConstraint("ck_data_subject_requests_origin",
                "((kind IN ('export','erasure') AND origin IN ('self','administrator')) OR (kind = 'erasure' AND origin = 'closure_fanout') OR (kind = 'closure' AND origin = 'self')) IS TRUE");
            table.HasCheckConstraint("ck_data_subject_requests_requester",
                "((origin = 'self' AND requester_user_id = subject_user_id) OR (origin = 'administrator' AND requester_user_id IS NOT NULL AND requester_user_id <> subject_user_id) OR (origin = 'closure_fanout' AND requester_user_id IS NULL)) IS TRUE");
            table.HasCheckConstraint("ck_data_subject_requests_status", """
                ((kind = 'export' AND status IN ('requested','fulfilling','completed','failed','cancelled'))
                OR (kind = 'erasure' AND status IN ('scheduled','fulfilling','awaiting_privileged_step','completed','blocked','failed','cancelled'))
                OR (kind = 'closure' AND status IN ('scheduled','fulfilling','completed','cancelled'))) IS TRUE
                """);
            table.HasCheckConstraint("ck_data_subject_requests_window",
                "((kind IN ('erasure','closure') AND origin <> 'closure_fanout' AND execute_at = created_at + interval '24 hours') OR ((kind = 'export' OR origin = 'closure_fanout') AND execute_at IS NULL)) IS TRUE");
            table.HasCheckConstraint("ck_data_subject_requests_snapshot", """
                CASE WHEN jsonb_typeof(membership_snapshot) = 'array'
                    THEN jsonb_array_length(membership_snapshot) BETWEEN 1 AND 256
                        AND NOT jsonb_path_exists(membership_snapshot,
                            '$[*] ? (@.type() != "string" || !(@ like_regex "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"))')
                    ELSE false END
                """);
            table.HasCheckConstraint("ck_data_subject_requests_progress", """
                CASE WHEN jsonb_typeof(progress) = 'object'
                    THEN jsonb_array_length(jsonb_path_query_array(progress, '$.keyvalue()')) <= 32
                        AND NOT jsonb_path_exists(progress,
                            '$.keyvalue() ? (!(@.key like_regex "^[a-z][a-z0-9_]{0,31}$") || @.value.type() != "string" || !(@.value like_regex "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[.][0-9]{7}Z$"))')
                    ELSE false END
                """);
            table.HasCheckConstraint("ck_data_subject_requests_result", """
                CASE WHEN jsonb_typeof(result) = 'object'
                    THEN NOT jsonb_path_exists(result,
                        '$.keyvalue() ? (!(@.key like_regex "^(consent_evidence|invitation_record|invitation_envelope|audit_actor_reference|audit_entity_reference|audit_metadata_reference|unscoped_audit_rows|transport_evidence|execution_evidence|provider_event_record|idempotency_cache|attribution_reference|federation_actor_reference|request_receipt|external_copy|unserved_group)$") || @.value.type() != "number" || @.value < 0 || @.value > 9007199254740991 || @.value.floor() != @.value)')
                    ELSE false END
                """);
            table.HasCheckConstraint("ck_data_subject_requests_outcome", """
                ((status = 'completed') = (completed_at IS NOT NULL)
                    AND (completed_at IS NULL OR (isfinite(completed_at) AND completed_at >= created_at))
                    AND ((status = 'failed' AND failure_reason IN ('export_too_large','execution_failed')
                            AND (failure_reason <> 'export_too_large' OR kind = 'export'))
                        OR (status <> 'failed' AND failure_reason IS NULL))
                    AND (last_refusal IS NULL OR last_refusal IN ('blocked_window','blocked_hold','blocked_last_administrator'))
                    AND (status <> 'blocked' OR last_refusal IN ('blocked_hold','blocked_last_administrator'))) IS TRUE
                """);
            table.HasCheckConstraint("ck_data_subject_requests_times",
                "(isfinite(created_at) AND (updated_at IS NULL OR (isfinite(updated_at) AND updated_at >= created_at)) AND (execute_at IS NULL OR isfinite(execute_at))) IS TRUE");
            table.HasCheckConstraint("ck_data_subject_requests_no_delete", "deleted_at IS NULL AND deleted_by IS NULL");
        });
        builder.HasKey(entity => entity.Id).HasName("pk_data_subject_requests");
        builder.Property(entity => entity.Id)
            .HasConversion<DataSubjectRequestId.EfCoreValueConverter, DataSubjectRequestId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Scope(Table);
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id }).HasName("ux_data_subject_requests_tenant_id_id");
        builder.Property(entity => entity.Kind).HasConversion(new ClosedCodeConverter<DataRequestKind>(DataRequestCodes.Kinds))
            .HasColumnType("text");
        builder.Property(entity => entity.Origin).HasConversion(new ClosedCodeConverter<DataRequestOrigin>(DataRequestCodes.Origins))
            .HasColumnType("text");
        builder.Property(entity => entity.Status).HasConversion(new ClosedCodeConverter<DataRequestStatus>(DataRequestCodes.Statuses))
            .HasColumnType("text");
        builder.Property(entity => entity.LastRefusal)
            .HasConversion(new ClosedCodeConverter<DataRequestRefusal>(DataRequestCodes.Refusals)).HasColumnType("text");
        builder.Property(entity => entity.FailureReason)
            .HasConversion(new ClosedCodeConverter<DataRequestFailureReason>(DataRequestCodes.FailureReasons)).HasColumnType("text");
        builder.Property(entity => entity.SubjectUserId).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(entity => entity.RequesterUserId).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(entity => entity.MembershipSnapshotJson).HasColumnName("membership_snapshot").HasColumnType("jsonb").IsRequired();
        builder.Property(entity => entity.ProgressJson).HasColumnName("progress").HasColumnType("jsonb")
            .HasDefaultValue(DataRequestDocuments.EmptyObject).IsRequired();
        builder.Property(entity => entity.ResultJson).HasColumnName("result").HasColumnType("jsonb")
            .HasDefaultValue(DataRequestDocuments.EmptyObject).IsRequired();
        builder.Ignore(entity => entity.Group);
        builder.Ignore(entity => entity.SnapshotMembershipIds);
        builder.Ignore(entity => entity.Progress);
        builder.Ignore(entity => entity.RetainedReport);
        builder.Ignore(entity => entity.IsTerminal);
        builder.HasOne<User>().WithMany().HasForeignKey(entity => entity.SubjectUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_data_subject_requests_subject");
        builder.HasIndex(entity => entity.SubjectUserId).HasDatabaseName("ix_data_subject_requests_subject_user_id");
        builder.HasOne<User>().WithMany().HasForeignKey(entity => entity.RequesterUserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_data_subject_requests_requester");
        builder.HasIndex(entity => entity.RequesterUserId).HasDatabaseName("ix_data_subject_requests_requester_user_id");
        // ADR-0106 § 7: one non-terminal request per kind, subject and group, so two concurrent creations
        // cannot both pass; every terminal status frees the slot.
        builder.HasIndex(entity => new { entity.TenantId, entity.OrganizationId, entity.SubjectUserId, entity.Kind })
            .IsUnique().AreNullsDistinct(false).HasFilter(ActiveFilter).HasDatabaseName("ux_data_subject_requests_active");
        builder.MapAuditColumns<DataSubjectRequest, DataSubjectRequestId>();
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "A person's request in one scope group: subject and requester, the group and the frozen Membership linkage.",
            nameof(DataSubjectRequest.Id), nameof(DataSubjectRequest.TenantId), nameof(DataSubjectRequest.OrganizationId),
            nameof(DataSubjectRequest.SubjectUserId), nameof(DataSubjectRequest.RequesterUserId),
            nameof(DataSubjectRequest.MembershipSnapshotJson));
        builder.DeclarePersonalData(PersonalDataCategory.None,
            "Closed request kind, origin, status, window, refusal and failure codes, participant progress and retained-kind counts.",
            nameof(DataSubjectRequest.Kind), nameof(DataSubjectRequest.Origin), nameof(DataSubjectRequest.Status),
            nameof(DataSubjectRequest.ExecuteAt), nameof(DataSubjectRequest.ProgressJson), nameof(DataSubjectRequest.ResultJson),
            nameof(DataSubjectRequest.LastRefusal), nameof(DataSubjectRequest.FailureReason), nameof(DataSubjectRequest.CompletedAt));
    }
}

/// <summary>
/// <c>data_export_artifacts</c> (ADR-0107 § 4): receipt-safe metadata of an encrypted bundle, mirroring
/// its request's group and subject.
/// </summary>
internal sealed class DataExportArtifactConfiguration : IEntityTypeConfiguration<DataExportArtifact>
{
    internal const string Table = "data_export_artifacts";

    public void Configure(EntityTypeBuilder<DataExportArtifact> builder)
    {
        builder.ToTable(Table, table =>
        {
            table.HasCheckConstraint("ck_data_export_artifacts_identifiers",
                "(uuid_extract_version(id)=7 AND uuid_extract_version(subject_user_id)=7 AND subject_user_id <> '00000000-0000-7000-8000-000000000001'::uuid) IS TRUE");
            table.HasCheckConstraint("ck_data_export_artifacts_tenant",
                "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
            table.HasCheckConstraint("ck_data_export_artifacts_status", "status IN ('available','revoked','expired','purged')");
            table.HasCheckConstraint("ck_data_export_artifacts_size",
                "byte_count BETWEEN 1 AND 67108864 AND chunk_count BETWEEN 1 AND 512 AND chunk_count = (byte_count + 131071) / 131072");
            table.HasCheckConstraint("ck_data_export_artifacts_key_version", "key_version COLLATE \"C\" ~ '^[A-Za-z0-9_-]{1,32}$'");
            table.HasCheckConstraint("ck_data_export_artifacts_times",
                "(isfinite(created_at) AND expires_at = created_at + interval '7 days' AND (updated_at IS NULL OR (isfinite(updated_at) AND updated_at >= created_at))) IS TRUE");
            table.HasCheckConstraint("ck_data_export_artifacts_lifecycle",
                "((status IN ('revoked','purged')) = (purged_at IS NOT NULL) AND (purged_at IS NULL OR (isfinite(purged_at) AND purged_at >= created_at))) IS TRUE");
            table.HasCheckConstraint("ck_data_export_artifacts_no_delete", "deleted_at IS NULL AND deleted_by IS NULL");
        });
        builder.HasKey(entity => entity.Id).HasName("pk_data_export_artifacts");
        builder.Property(entity => entity.Id)
            .HasConversion<DataExportArtifactId.EfCoreValueConverter, DataExportArtifactId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Scope(Table);
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id }).HasName("ux_data_export_artifacts_tenant_id_id");
        builder.Property(entity => entity.RequestId)
            .HasConversion<DataSubjectRequestId.EfCoreValueConverter, DataSubjectRequestId.EfCoreValueComparer>();
        builder.Property(entity => entity.SubjectUserId).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(entity => entity.Status)
            .HasConversion(new ClosedCodeConverter<DataExportArtifactStatus>(DataRequestCodes.ArtifactStatuses)).HasColumnType("text");
        builder.Property(entity => entity.KeyVersion).HasMaxLength(DataRequestLimits.MaximumKeyVersionLength).UseCollation("C");
        builder.HasOne<DataSubjectRequest>().WithMany().HasForeignKey(entity => new { entity.TenantId, entity.RequestId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_data_export_artifacts_request");
        // One artifact per export request; the index also supports the request foreign key.
        builder.HasIndex(entity => new { entity.TenantId, entity.RequestId }).IsUnique().HasDatabaseName("ux_data_export_artifacts_request");
        // ADR-0107 § 4: a subject has at most one available artifact per group.
        builder.HasIndex(entity => new { entity.TenantId, entity.OrganizationId, entity.SubjectUserId })
            .IsUnique().AreNullsDistinct(false).HasFilter("status = 'available'").HasDatabaseName("ux_data_export_artifacts_available");
        builder.MapAuditColumns<DataExportArtifact, DataExportArtifactId>();
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "A person's export receipt: the group, the request and the subject it was generated for.",
            nameof(DataExportArtifact.Id), nameof(DataExportArtifact.TenantId), nameof(DataExportArtifact.OrganizationId),
            nameof(DataExportArtifact.RequestId), nameof(DataExportArtifact.SubjectUserId));
        builder.DeclarePersonalData(PersonalDataCategory.None,
            "Closed artifact lifecycle, plaintext size, chunk count, key-ring version label and expiry; no content or key.",
            nameof(DataExportArtifact.Status), nameof(DataExportArtifact.ByteCount), nameof(DataExportArtifact.ChunkCount),
            nameof(DataExportArtifact.KeyVersion), nameof(DataExportArtifact.ExpiresAt), nameof(DataExportArtifact.PurgedAt));
    }
}

/// <summary>
/// <c>data_export_tickets</c> (ADR-0107 § 4): digest-only download cursors bound to a session; the
/// application role may set only the consumed marker.
/// </summary>
internal sealed class DataExportTicketConfiguration : IEntityTypeConfiguration<DataExportTicket>
{
    internal const string Table = "data_export_tickets";

    public void Configure(EntityTypeBuilder<DataExportTicket> builder)
    {
        builder.ToTable(Table, table =>
        {
            table.HasCheckConstraint("ck_data_export_tickets_identifiers",
                "(uuid_extract_version(id)=7 AND uuid_extract_version(created_by)=7 AND created_by <> '00000000-0000-7000-8000-000000000001'::uuid) IS TRUE");
            table.HasCheckConstraint("ck_data_export_tickets_tenant",
                "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
            table.HasCheckConstraint("ck_data_export_tickets_digests", "octet_length(digest) = 32 AND octet_length(session_binding_digest) = 32");
            table.HasCheckConstraint("ck_data_export_tickets_times",
                "(isfinite(created_at) AND expires_at = created_at + interval '15 minutes' AND (consumed_at IS NULL OR (consumed_at >= created_at AND consumed_at < expires_at))) IS TRUE");
        });
        builder.HasKey(entity => entity.Id).HasName("pk_data_export_tickets");
        builder.Property(entity => entity.Id)
            .HasConversion<DataExportTicketId.EfCoreValueConverter, DataExportTicketId.EfCoreValueComparer>().ValueGeneratedNever();
        builder.Scope(Table);
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id }).HasName("ux_data_export_tickets_tenant_id_id");
        builder.Property(entity => entity.ArtifactId)
            .HasConversion<DataExportArtifactId.EfCoreValueConverter, DataExportArtifactId.EfCoreValueComparer>();
        builder.Property(entity => entity.CreatedBy).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        foreach (var digest in new[] { nameof(DataExportTicket.Digest), nameof(DataExportTicket.SessionBindingDigest) })
            builder.Property<string>(digest).HasConversion(
                value => Convert.FromHexString(value),
                value => Convert.ToHexStringLower(value)).HasColumnType("bytea");
        builder.HasOne<DataExportArtifact>().WithMany().HasForeignKey(entity => new { entity.TenantId, entity.ArtifactId })
            .HasPrincipalKey(entity => new { entity.TenantId, entity.Id })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_data_export_tickets_artifact");
        builder.HasIndex(entity => new { entity.TenantId, entity.ArtifactId }).HasDatabaseName("ix_data_export_tickets_tenant_id_artifact_id");
        builder.HasIndex(entity => new { entity.TenantId, entity.Digest }).IsUnique().HasDatabaseName("ux_data_export_tickets_digest");
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "A person's download cursor: the group, the artifact, the minting subject and its session-bound credential digests.",
            nameof(DataExportTicket.Id), nameof(DataExportTicket.TenantId), nameof(DataExportTicket.OrganizationId),
            nameof(DataExportTicket.ArtifactId), nameof(DataExportTicket.CreatedBy), nameof(DataExportTicket.Digest),
            nameof(DataExportTicket.SessionBindingDigest));
        builder.DeclarePersonalData(PersonalDataCategory.Audit, "Record when the ticket was minted.", nameof(DataExportTicket.CreatedAt));
        builder.DeclarePersonalData(PersonalDataCategory.None,
            "The ticket's fixed expiry and the instant its download finished.",
            nameof(DataExportTicket.ExpiresAt), nameof(DataExportTicket.ConsumedAt));
    }
}
