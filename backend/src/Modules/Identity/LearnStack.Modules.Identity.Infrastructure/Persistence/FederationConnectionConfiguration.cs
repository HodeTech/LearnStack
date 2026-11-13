using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// ADR-0099's tenant-wide connection registry. No secret, token, assertion or certificate column exists;
/// a Deleted connection is a tombstone that keeps its alias reserved (ADR-0099 Amendment 1).
/// </summary>
internal sealed class FederationConnectionConfiguration : IEntityTypeConfiguration<FederationConnection>
{
    internal const string Table = "federation_connections";

    public void Configure(EntityTypeBuilder<FederationConnection> builder)
    {
        builder.ToTable(Table, table =>
        {
            table.HasCheckConstraint("ck_federation_connections_identifiers",
                "(uuid_extract_version(id)=7 AND (pending_operation_id IS NULL OR uuid_extract_version(pending_operation_id)=7) AND (test_attempt_id IS NULL OR uuid_extract_version(test_attempt_id)=7)) IS TRUE");
            table.HasCheckConstraint("ck_federation_connections_tenant",
                "tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,'00000000-0000-7000-8000-000000000002'::uuid)");
            table.HasCheckConstraint("ck_federation_connections_alias", "alias = 'lsf-' || replace(id::text, '-', '')");
            table.HasCheckConstraint("ck_federation_connections_protocol", "protocol IN ('Oidc','Saml')");
            table.HasCheckConstraint("ck_federation_connections_state",
                "state IN ('Draft','TestOpen','TestPassed','Active','Disabled','Deleted')");
            table.HasCheckConstraint("ck_federation_connections_source_kind", "source_kind IN ('MetadataUrl','DiscoveryUrl','Manual')");
            table.HasCheckConstraint("ck_federation_connections_display_name",
                "char_length(display_name) BETWEEN 1 AND 80 AND display_name = btrim(display_name, ' ') AND display_name !~ '[[:cntrl:]]'");
            table.HasCheckConstraint("ck_federation_connections_issuer",
                "octet_length(issuer_or_entity_id) BETWEEN 1 AND 2048 AND issuer_or_entity_id ~ '^[!-~]+$'");
            table.HasCheckConstraint("ck_federation_connections_client",
                "((protocol <> 'Oidc' OR (client_id IS NOT NULL AND octet_length(client_id) BETWEEN 1 AND 255 AND client_id ~ '^[!-~]+$')) AND (protocol <> 'Saml' OR client_id IS NULL)) IS TRUE");
            table.HasCheckConstraint("ck_federation_connections_revision", "configuration_revision >= 1");
            table.HasCheckConstraint("ck_federation_connections_digests",
                "(configuration_digest ~ '^[0-9a-f]{64}$' AND desired_digest ~ '^[0-9a-f]{64}$' AND (last_provider_digest IS NULL OR last_provider_digest ~ '^[0-9a-f]{64}$') AND (pending_desired_digest IS NULL OR pending_desired_digest ~ '^[0-9a-f]{64}$')) IS TRUE");
            table.HasCheckConstraint("ck_federation_connections_pending", """
                ((pending_operation_kind IS NULL AND pending_operation_id IS NULL AND pending_actor IS NULL AND pending_started_at IS NULL
                    AND pending_quiet_until IS NULL AND pending_desired_digest IS NULL)
                OR (pending_operation_kind IN ('Configure','RotateSecret','Rename','TestStart','Disable','Delete')
                    AND pending_operation_id IS NOT NULL AND pending_actor IS NOT NULL AND pending_started_at IS NOT NULL
                    AND pending_quiet_until IS NOT NULL AND pending_desired_digest IS NOT NULL
                    AND isfinite(pending_started_at) AND pending_quiet_until = pending_started_at + interval '90 seconds'
                    AND state <> 'Deleted')) IS TRUE
                """);
            table.HasCheckConstraint("ck_federation_connections_test", """
                ((state IN ('TestOpen','TestPassed') AND test_attempt_id IS NOT NULL AND test_actor IS NOT NULL AND test_revision IS NOT NULL
                    AND test_revision = configuration_revision AND test_expires_at IS NOT NULL AND isfinite(test_expires_at)
                    AND ((state='TestOpen' AND test_result='Pending' AND test_passed_at IS NULL)
                        OR (state='TestPassed' AND test_result='Passed' AND test_passed_at IS NOT NULL
                            AND test_expires_at = test_passed_at + interval '10 minutes')))
                OR (state NOT IN ('TestOpen','TestPassed') AND test_attempt_id IS NULL AND test_actor IS NULL AND test_revision IS NULL
                    AND test_expires_at IS NULL AND test_result IS NULL AND test_passed_at IS NULL)) IS TRUE
                """);
            table.HasCheckConstraint("ck_federation_connections_tombstone",
                "((state='Deleted' AND deleted_at IS NOT NULL AND deleted_by IS NOT NULL) OR (state<>'Deleted' AND deleted_at IS NULL AND deleted_by IS NULL)) IS TRUE");
        });
        builder.HasKey(entity => entity.Id).HasName("pk_federation_connections");
        builder.Property(entity => entity.Id).HasConversion<FederationConnectionId.EfCoreValueConverter, FederationConnectionId.EfCoreValueComparer>()
            .ValueGeneratedNever();
        builder.Tenant();
        builder.HasAlternateKey(entity => new { entity.TenantId, entity.Id }).HasName("ux_federation_connections_tenant_id_id");
        // One connection per tenant among live rows; a tombstone frees the slot and keeps its own alias.
        builder.HasIndex(entity => entity.TenantId).IsUnique().HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_federation_connections_tenant_live");
        builder.Property(entity => entity.Protocol).HasEnumAsText();
        builder.Property(entity => entity.State).HasEnumAsText();
        builder.Property(entity => entity.SourceKind).HasEnumAsText();
        builder.Property(entity => entity.Alias).HasMaxLength(FederationConnection.AliasLength).UseCollation("C");
        builder.Property(entity => entity.DisplayName).HasMaxLength(FederationDisplayName.MaximumLength);
        builder.Property(entity => entity.IssuerOrEntityId).HasMaxLength(FederationConnection.MaximumIssuerLength).UseCollation("C");
        builder.Property(entity => entity.ClientId).HasMaxLength(FederationConnection.MaximumClientIdLength).UseCollation("C");
        foreach (var digest in new[] { nameof(FederationConnection.ConfigurationDigest), nameof(FederationConnection.DesiredDigest),
                     nameof(FederationConnection.LastProviderDigest), nameof(FederationConnection.PendingDesiredDigest) })
            builder.Property<string>(digest).HasMaxLength(FederationConnection.DigestLength).UseCollation("C");
        builder.Property(entity => entity.PendingOperationKind).HasConversion<string>().HasColumnType("text");
        builder.Property(entity => entity.PendingOperationId)
            .HasConversion<FederationOperationId.EfCoreValueConverter, FederationOperationId.EfCoreValueComparer>();
        builder.Property(entity => entity.PendingActor).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(entity => entity.TestAttemptId)
            .HasConversion<FederationTestAttemptId.EfCoreValueConverter, FederationTestAttemptId.EfCoreValueComparer>();
        builder.Property(entity => entity.TestActor).HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(entity => entity.TestResult).HasConversion<string>().HasColumnType("text");
        builder.Ignore(entity => entity.HasPendingOperation);
        builder.MapAuditColumns<FederationConnection, FederationConnectionId>();
        builder.DeclarePersonalData(PersonalDataCategory.None,
            "Institution-owned federation configuration: protocol, derived alias, public IdP identifiers, nonsecret digests and lifecycle.",
            nameof(FederationConnection.Id), nameof(FederationConnection.TenantId), nameof(FederationConnection.Protocol),
            nameof(FederationConnection.Alias), nameof(FederationConnection.DisplayName), nameof(FederationConnection.IssuerOrEntityId),
            nameof(FederationConnection.State), nameof(FederationConnection.SourceKind), nameof(FederationConnection.ClientId),
            nameof(FederationConnection.ConfigurationRevision), nameof(FederationConnection.ConfigurationDigest),
            nameof(FederationConnection.DesiredDigest), nameof(FederationConnection.LastProviderDigest),
            nameof(FederationConnection.PendingOperationKind), nameof(FederationConnection.PendingOperationId),
            nameof(FederationConnection.PendingStartedAt), nameof(FederationConnection.PendingQuietUntil),
            nameof(FederationConnection.PendingDesiredDigest), nameof(FederationConnection.TestAttemptId),
            nameof(FederationConnection.TestRevision), nameof(FederationConnection.TestExpiresAt),
            nameof(FederationConnection.TestResult), nameof(FederationConnection.TestPassedAt));
        builder.DeclarePersonalData(PersonalDataCategory.Identity,
            "Pseudonymous actor identifiers attributing a pending provider write or a test attempt.",
            nameof(FederationConnection.PendingActor), nameof(FederationConnection.TestActor));
    }
}
