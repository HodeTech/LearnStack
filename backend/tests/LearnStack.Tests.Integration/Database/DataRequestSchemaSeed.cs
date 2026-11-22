using Npgsql;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0106/0107 rows for the nonempty isolation census, written by the application role under the
/// canonical fences. Tenant A holds a completed export (artifact, ticket and chunk) in its tenant-wide
/// group and in each of its two organizations; tenant B holds one in its tenant-wide group and a
/// requested export in its organization.
/// </summary>
internal static class DataRequestSchemaSeed
{
    internal static readonly Guid SystemActor = Guid.Parse("00000000-0000-7000-8000-000000000001");

    internal sealed record Row(Guid Tenant, Guid? Organization, Guid Subject, Guid Membership, Guid Request,
        Guid? Artifact, Guid? Ticket);

    internal static readonly Row TenantWideA = new(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA,
        Guid.Parse("01930000-0000-7000-8000-00000000da01"), Guid.Parse("01930000-0000-7000-8000-00000000db01"),
        Guid.Parse("01930000-0000-7000-8000-00000000dc01"));

    internal static readonly Row BranchA1 = new(SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, BranchMembershipA1,
        Guid.Parse("01930000-0000-7000-8000-00000000da02"), Guid.Parse("01930000-0000-7000-8000-00000000db02"),
        Guid.Parse("01930000-0000-7000-8000-00000000dc02"));

    internal static readonly Row BranchA2 = new(SchemaFixture.TenantA, SchemaFixture.OrgA2, PersonA, BranchMembershipA2,
        Guid.Parse("01930000-0000-7000-8000-00000000da03"), Guid.Parse("01930000-0000-7000-8000-00000000db03"),
        Guid.Parse("01930000-0000-7000-8000-00000000dc03"));

    internal static readonly Row TenantWideB = new(SchemaFixture.TenantB, null, AdminB, AdminMembershipB,
        Guid.Parse("01930000-0000-7000-8000-00000000da11"), Guid.Parse("01930000-0000-7000-8000-00000000db11"),
        Guid.Parse("01930000-0000-7000-8000-00000000dc11"));

    internal static readonly Row BranchB = new(SchemaFixture.TenantB, SchemaFixture.OrgB1, PersonB, BranchMembershipB,
        Guid.Parse("01930000-0000-7000-8000-00000000da12"), null, null);

    internal static readonly Row[] Rows = [TenantWideA, BranchA1, BranchA2, TenantWideB, BranchB];

    internal static async Task SeedAsync(string connectionString)
    {
        await using var connection = await DataExportTestKit.OpenAppAsync(connectionString);
        foreach (var row in Rows)
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await FenceAsync(connection, transaction, row.Tenant, row.Organization, row.Subject);
            await InsertRequestAsync(connection, transaction, row.Request, row.Tenant, row.Organization, row.Subject, [row.Membership]);
            if (row.Artifact is { } artifact)
                await CompleteExportAsync(connection, transaction, row, artifact, row.Ticket!.Value);
            await transaction.CommitAsync();
        }
    }

    /// <summary>Announces the group, then takes the subject's exclusive User fence and the shared tenant fence.</summary>
    internal static async Task FenceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant,
        Guid? organization, Guid? subject)
    {
        await ScopeAsync(connection, transaction, tenant, organization);
        await LockAsync(connection, transaction, tenant, subject, false);
    }

    internal static string Snapshot(params Guid[] memberships) =>
        "[" + string.Join(",", memberships.Select(id => "\"" + id.ToString("D") + "\"")) + "]";

    /// <summary>A self-filed request in its initial state; the clock is the transaction's.</summary>
    internal static Task InsertRequestAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid tenant,
        Guid? organization, Guid subject, Guid[] memberships, string kind = "export") =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                status,membership_snapshot,execute_at,created_at,created_by)
            VALUES(@id,@tenant,@organization,@kind,'self',@subject,@subject,
                CASE WHEN @kind='export' THEN 'requested' ELSE 'scheduled' END,@snapshot::jsonb,
                CASE WHEN @kind='export' THEN NULL ELSE now() + interval '24 hours' END,now(),@subject)
            """, ("id", id), ("tenant", tenant), ("organization", organization), ("kind", kind), ("subject", subject),
            ("snapshot", Snapshot(memberships)));

    /// <summary>One status step by the system actor, stamped with the transaction clock.</summary>
    internal static Task StepAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string set) =>
        ExecuteAsync(connection, transaction,
            $"UPDATE public.data_subject_requests SET {set},row_version=row_version+1,updated_at=now(),updated_by=@system WHERE id=@id",
            ("id", id), ("system", SystemActor));

    /// <summary>fulfilling, the artifact and its chunks, completed, then the subject's ticket.</summary>
    internal static async Task CompleteExportAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Row row,
        Guid artifact, Guid ticket)
    {
        await StepAsync(connection, transaction, row.Request, "status='fulfilling'");
        await InsertArtifactAsync(connection, transaction, artifact, row.Tenant, row.Organization, row.Request, row.Subject, 100);
        await InsertChunkAsync(connection, transaction, row.Tenant, row.Organization, artifact, 0);
        await StepAsync(connection, transaction, row.Request, "status='completed',completed_at=now()");
        await InsertTicketAsync(connection, transaction, ticket, row.Tenant, row.Organization, artifact, row.Subject);
    }

    internal static Task InsertArtifactAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid tenant,
        Guid? organization, Guid request, Guid subject, long bytes) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO public.data_export_artifacts(id,tenant_id,organization_id,request_id,subject_user_id,status,byte_count,
                chunk_count,key_version,expires_at,created_at,created_by)
            VALUES(@id,@tenant,@organization,@request,@subject,'available',@bytes,(@bytes + 131071) / 131072,'v1',
                now() + interval '7 days',now(),@system)
            """, ("id", id), ("tenant", tenant), ("organization", organization), ("request", request), ("subject", subject),
            ("bytes", bytes), ("system", SystemActor));

    internal static Task InsertChunkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant,
        Guid? organization, Guid artifact, int index, int nonce = 12, int ciphertext = 100, int tag = 16) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO public.data_export_chunks(tenant_id,organization_id,artifact_id,chunk_index,nonce,ciphertext,tag)
            VALUES(@tenant,@organization,@artifact,@index,@nonce,@ciphertext,@tag)
            """, ("tenant", tenant), ("organization", organization), ("artifact", artifact), ("index", index),
            ("nonce", new byte[nonce]), ("ciphertext", new byte[ciphertext]), ("tag", new byte[tag]));

    internal static Task InsertTicketAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid tenant,
        Guid? organization, Guid artifact, Guid subject) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO public.data_export_tickets(id,tenant_id,organization_id,artifact_id,digest,session_binding_digest,
                created_at,created_by,expires_at)
            VALUES(@id,@tenant,@organization,@artifact,sha256(uuid_send(@id)),sha256(uuid_send(@artifact)),
                now(),@subject,now() + interval '15 minutes')
            """, ("id", id), ("tenant", tenant), ("organization", organization), ("artifact", artifact), ("subject", subject));
}
