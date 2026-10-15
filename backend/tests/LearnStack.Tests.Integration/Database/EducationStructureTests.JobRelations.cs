using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    private sealed record JobParentGuard(string Table, string Function, int TriggerType, bool Deferred = false);
    private sealed record JobParentRelation(string Child, string Parent, string Constraint, string ChildColumns,
        string ParentColumns, bool Deferred, JobParentGuard[] Guards);
    private static readonly JobParentGuard JobEnvelope = new("background_jobs", "fn_background_jobs_envelope", 23);
    private static readonly JobParentGuard JobTransition = new("background_jobs", "fn_background_jobs_transition", 19);
    private static readonly JobParentGuard AttemptGuard = new("background_job_attempts", "fn_background_job_attempts_guard", 23);
    private static readonly JobParentGuard RecipientGuard = new("background_job_recipients", "fn_background_job_recipients_guard", 23);
    private static readonly JobParentGuard ChildReceipt = new("background_jobs", "fn_background_job_recipient_receipt", 21, true);
    private static readonly JobParentGuard RecipientReceipt = new("background_job_recipients", "fn_background_job_recipient_receipt", 21, true);

    // Raw Platform infrastructure has no EF relation. Composite job/attempt keys
    // preserve the job identity; its attempt guard independently mirrors the org.
    // Reciprocal job/recipient links additionally require deferred receipt guards.
    private static readonly JobParentRelation[] JobParentRelations =
    [
        new("background_jobs", "background_jobs", "fk_background_jobs_parent", "tenant_id,parent_job_id", "tenant_id,id", false,
            [JobEnvelope]),
        new("background_jobs", "background_job_attempts", "fk_background_jobs_current_attempt", "tenant_id,id,current_attempt_id", "tenant_id,job_id,id", true,
            [JobEnvelope, JobTransition, AttemptGuard]),
        new("background_jobs", "background_job_attempts", "fk_background_jobs_final_outcome", "tenant_id,id,final_outcome_attempt_id", "tenant_id,job_id,id", true,
            [JobEnvelope, JobTransition, AttemptGuard]),
        new("background_jobs", "background_job_recipients", "fk_background_jobs_recipient", "tenant_id,recipient_id", "tenant_id,id", false,
            [JobEnvelope, RecipientGuard, ChildReceipt, RecipientReceipt]),
        new("background_job_attempts", "background_jobs", "fk_background_job_attempts_job", "tenant_id,job_id", "tenant_id,id", false,
            [AttemptGuard]),
        new("background_job_attempts", "background_job_attempts", "fk_background_job_attempts_outcome", "tenant_id,job_id,final_outcome_attempt_id", "tenant_id,job_id,id", false,
            [AttemptGuard]),
        new("background_job_recipients", "background_jobs", "fk_background_job_recipients_job", "tenant_id,accepted_job_id", "tenant_id,id", true,
            [RecipientGuard, JobEnvelope, ChildReceipt, RecipientReceipt]),
    ];

    private static bool IsDeclaredJobRelation(ForeignKey key) => JobParentRelations.Any(relation =>
        relation.Child == key.Child && relation.Parent == key.Parent && relation.Constraint == key.Name);

    private static async Task<List<string>> JobParentGuardOffendersAsync(List<ForeignKey> applied, List<string[]> triggers)
    {
        var canonical = await BackgroundJobSchemaInventory.CanonicalSqlAsync();
        var offenders = new List<string>();
        foreach (var relation in JobParentRelations)
        {
            var keys = applied.Where(key => key.Child == relation.Child && key.Parent == relation.Parent
                && key.Name == relation.Constraint).ToList();
            if (keys.Count != 1)
                offenders.Add($"{relation.Child}: missing parent FK ({relation.Constraint})");
            else if (keys[0].ChildColumns != relation.ChildColumns || keys[0].ParentColumns != relation.ParentColumns
                || keys[0].DeleteAction != "r" || !keys[0].Validated || keys[0].Deferred != relation.Deferred)
                offenders.Add($"{relation.Child}: tenant-composite FK ({relation.Constraint})");
            foreach (var guard in relation.Guards)
            {
                var body = Regex.Match(canonical,
                    @"CREATE FUNCTION public\." + Regex.Escape(guard.Function)
                    + @"\(\) RETURNS trigger\s+LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS \$\$(?<body>.*?)\$\$;",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant);
                if (!body.Success || !triggers.Any(trigger => trigger[0] == guard.Table && trigger[1] == "public"
                    && trigger[3] == "true" && trigger[4] == guard.Function && trigger[5] == guard.TriggerType.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    && trigger[6] == (guard.Deferred ? "true" : "false") && trigger[7] == (guard.Deferred ? "true" : "false")
                    && NormalizeFunction(trigger[2]) == NormalizeFunction(body.Groups["body"].Value)))
                    offenders.Add($"{relation.Child}: parent guard ({guard.Table}.{guard.Function})");
            }
        }
        return offenders.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string NormalizeFunction(string body) => Regex.Replace(body.Trim(), @"\s+", " ");

    private static async Task AssertJobParentMutationsAsync(DbConnection owner, DbTransaction transaction, string table)
    {
        var relations = JobParentRelations.Where(relation => relation.Child == table).ToArray();
        relations.Should().NotBeEmpty();
        foreach (var guard in relations.SelectMany(relation => relation.Guards).Distinct())
        {
            var trigger = await DefinitionAsync(owner, transaction,
                $"SELECT pg_get_triggerdef(t.oid) FROM pg_trigger t WHERE t.tgrelid = '{guard.Table}'::regclass "
                + $"AND t.tgfoid = 'public.{guard.Function}()'::regprocedure AND NOT t.tgisinternal");
            var triggerName = await DefinitionAsync(owner, transaction,
                $"SELECT t.tgname FROM pg_trigger t WHERE t.tgrelid = '{guard.Table}'::regclass "
                + $"AND t.tgfoid = 'public.{guard.Function}()'::regprocedure AND NOT t.tgisinternal");
            await SchemaQueries.ExecuteAsync(owner, transaction, $"DROP TRIGGER {triggerName} ON {guard.Table}");
            (await ParentGuardOffendersAsync(owner, transaction)).Should().Contain(
                $"{table}: parent guard ({guard.Table}.{guard.Function})");
            await SchemaQueries.ExecuteAsync(owner, transaction, trigger);
            (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();

            var function = await DefinitionAsync(owner, transaction,
                $"SELECT pg_get_functiondef('public.{guard.Function}()'::regprocedure)");
            await SchemaQueries.ExecuteAsync(owner, transaction,
                $"CREATE OR REPLACE FUNCTION public.{guard.Function}() RETURNS trigger LANGUAGE plpgsql "
                + "SECURITY INVOKER SET search_path = pg_catalog AS $probe$BEGIN RETURN NEW; END;$probe$");
            (await ParentGuardOffendersAsync(owner, transaction)).Should().Contain(
                $"{table}: parent guard ({guard.Table}.{guard.Function})");
            await SchemaQueries.ExecuteAsync(owner, transaction, function);
            (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
        }
        foreach (var relation in relations)
        {
            var key = (await ReadForeignKeysAsync(owner, transaction)).Single(key => key.Name == relation.Constraint);
            await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE {table} DROP CONSTRAINT {key.Name}");
            (await ParentGuardOffendersAsync(owner, transaction)).Should().Contain($"{table}: missing parent FK ({key.Name})");
            await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE {table} ADD CONSTRAINT {key.Name} {key.Definition}");
            (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
        }
    }
}
