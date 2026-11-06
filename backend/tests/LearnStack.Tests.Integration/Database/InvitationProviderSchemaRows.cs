using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Valid synthetic provider rows for independently isolated policy and immutable-binding controls.</summary>
internal static class InvitationProviderSchemaRows
{
    internal static Task InsertAsync(NpgsqlConnection app, NpgsqlTransaction transaction,
        bool envelope, Guid id, BackgroundJobSchemaSeed.Scope job) => envelope
        ? InsertEnvelopeAsync(app, transaction, id, job)
        : SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO public.invitation_email_attempt_receipts
                (tenant_id,organization_id,envelope_id,job_id,attempt_id,operation_id,plan_fingerprint,
                 claim_id,admission_token,original_deadline,state)
            VALUES(@tenant,@org,@id,@job,@attempt,uuidv7(),repeat('a',64),uuidv7(),uuidv7(),
                clock_timestamp()+interval '1 minute','sending')
            """, ("tenant", job.TenantId), ("org", (object?)job.OrganizationId ?? DBNull.Value),
            ("id", id), ("job", job.JobId), ("attempt", job.AttemptId));

    internal static Task InsertEnvelopeAsync(NpgsqlConnection app, NpgsqlTransaction transaction,
        Guid id, BackgroundJobSchemaSeed.Scope job) => SchemaQueries.ExecuteAsync(app, transaction, """
            INSERT INTO public.invitation_email_envelopes
                (id,tenant_id,organization_id,invitation_id,generation_id,job_id,format_version,key_version,nonce,ciphertext,tag)
            VALUES(@id,@tenant,@org,uuidv7(),uuidv7(),@job,1,'fixture',decode(repeat('01',12),'hex'),
                decode('01','hex'),decode(repeat('02',16),'hex'))
            """, ("id", id), ("tenant", job.TenantId),
            ("org", (object?)job.OrganizationId ?? DBNull.Value), ("job", job.JobId));
}
