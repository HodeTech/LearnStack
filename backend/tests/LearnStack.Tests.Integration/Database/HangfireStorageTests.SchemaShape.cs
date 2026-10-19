using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireStorageTests
{
    [Theory]
    [InlineData("ALTER TABLE public.\"set\" DROP CONSTRAINT set_key_value_key",
        "ALTER TABLE public.\"set\" ADD CONSTRAINT set_key_value_key UNIQUE (key,value)")]
    [InlineData("ALTER TABLE public.counter ALTER COLUMN value TYPE integer",
        "ALTER TABLE public.counter ALTER COLUMN value TYPE bigint")]
    [InlineData("ALTER TABLE public.jobqueue ALTER COLUMN id DROP DEFAULT",
        "ALTER TABLE public.jobqueue ALTER COLUMN id SET DEFAULT nextval('public.jobqueue_id_seq'::regclass)")]
    [InlineData("ALTER TABLE public.jobqueue ALTER COLUMN jobid DROP NOT NULL",
        "ALTER TABLE public.jobqueue ALTER COLUMN jobid SET NOT NULL")]
    [InlineData("ALTER TABLE public.jobqueue DROP CONSTRAINT jobqueue_pkey",
        "ALTER TABLE public.jobqueue ADD CONSTRAINT jobqueue_pkey PRIMARY KEY (id)")]
    [InlineData("ALTER TABLE public.state DROP CONSTRAINT state_jobid_fkey",
        "ALTER TABLE public.state ADD CONSTRAINT state_jobid_fkey FOREIGN KEY (jobid) REFERENCES public.job(id) ON UPDATE CASCADE ON DELETE CASCADE")]
    [InlineData("DROP INDEX public.ix_hangfire_jobqueue_fetchedat_queue_jobid",
        "CREATE INDEX ix_hangfire_jobqueue_fetchedat_queue_jobid ON public.jobqueue USING btree (fetchedat NULLS FIRST,queue,jobid)")]
    [InlineData("ALTER SEQUENCE public.job_id_seq OWNED BY NONE",
        "ALTER SEQUENCE public.job_id_seq OWNED BY public.job.id")]
    [InlineData("ALTER SEQUENCE public.job_id_seq INCREMENT BY 2",
        "ALTER SEQUENCE public.job_id_seq INCREMENT BY 1")]
    [InlineData("ALTER TABLE public.hash DROP CONSTRAINT hash_key_field_key; ALTER TABLE public.hash ADD CONSTRAINT hash_key_field_key UNIQUE (key,value)",
        "ALTER TABLE public.hash DROP CONSTRAINT hash_key_field_key; ALTER TABLE public.hash ADD CONSTRAINT hash_key_field_key UNIQUE (key,field)")]
    [InlineData("DROP INDEX public.ix_hangfire_jobqueue_fetchedat_queue_jobid; CREATE INDEX ix_hangfire_jobqueue_fetchedat_queue_jobid ON public.jobqueue USING btree (fetchedat NULLS LAST,queue,jobid)",
        "DROP INDEX public.ix_hangfire_jobqueue_fetchedat_queue_jobid; CREATE INDEX ix_hangfire_jobqueue_fetchedat_queue_jobid ON public.jobqueue USING btree (fetchedat NULLS FIRST,queue,jobid)")]
    public async Task Stock_schema_shape_drift_refuses_before_provider_construction_and_restoration_reopens_storage(
        string mutation, string restoration)
    {
        await using var source = Source();
        var registry = BackgroundJobInfrastructureRegistry.CreateDefault();
        using (var healthy = HangfireStorageBootstrap.Create(source, registry, Diagnostics()).GetConnection())
            healthy.Should().NotBeNull("the real role and populated original schema must pass before mutation");
        try
        {
            await OwnerAsync(mutation);
            var create = () => HangfireStorageBootstrap.Create(source, registry, Diagnostics());
            create.Should().Throw<HangfireStorageRefusedException>().Which.InnerException.Should().BeNull();
        }
        finally { await OwnerAsync(restoration); }
        using var restored = HangfireStorageBootstrap.Create(source, registry, Diagnostics()).GetConnection();
        restored.Should().NotBeNull("restoring the exact stock fact must clear the same startup refusal");
    }
}
