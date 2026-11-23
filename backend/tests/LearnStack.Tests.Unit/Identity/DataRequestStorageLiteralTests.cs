using System.Globalization;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// The storage of ADR-0106 and ADR-0107 repeats <see cref="DataRequestLimits"/> as SQL literals, and classifies the
/// receipt columns: both are read here from the real model and DDL, so a constant changed on one side fails the build
/// instead of drifting.
/// </summary>
public sealed class DataRequestStorageLiteralTests
{
    private const string ModelOnly = "Host=model-only;Database=model-only;Username=model-only";

    [Fact]
    public void Every_bound_the_checks_repeat_is_the_shared_constant()
    {
        using var identity = Identity();
        var model = identity.GetService<IDesignTimeModel>().Model;
        var invariant = CultureInfo.InvariantCulture;

        Check(model, typeof(DataExportArtifact), "ck_data_export_artifacts_size").Should().Be(string.Format(invariant,
            "byte_count BETWEEN 1 AND {0} AND chunk_count BETWEEN 1 AND {1} AND chunk_count = (byte_count + {2}) / {3}",
            DataRequestLimits.MaximumBundleBytes, DataRequestLimits.MaximumChunkCount, DataRequestLimits.ChunkPlaintextBytes - 1,
            DataRequestLimits.ChunkPlaintextBytes));
        Check(model, typeof(DataExportArtifact), "ck_data_export_artifacts_times").Should().Contain(string.Format(invariant,
            "expires_at = created_at + interval '{0} days'", DataRequestLimits.ArtifactLifetime.TotalDays));
        Check(model, typeof(DataExportArtifact), "ck_data_export_artifacts_key_version").Should().Contain(string.Format(invariant,
            "{{1,{0}}}", DataRequestLimits.MaximumKeyVersionLength));
        var truncation = Check(model, typeof(DataExportArtifact), "ck_data_export_artifacts_truncation");
        truncation.Should().Contain(string.Format(invariant, "<= {0}", DataRequestLimits.MaximumTruncatedCollections))
            .And.Contain(DataRequestLimits.MaximumTruncationCount.ToString(invariant))
            .And.Contain(string.Format(invariant, "{{0,{0}}}", DataRequestLimits.MaximumParticipantKeyLength - 1))
            .And.Contain(string.Format(invariant, "{{0,{0}}}", DataRequestLimits.MaximumCollectionNameLength - 1));
        Check(model, typeof(DataExportTicket), "ck_data_export_tickets_times").Should().Contain(string.Format(invariant,
            "expires_at = created_at + interval '{0} minutes'", DataRequestLimits.TicketLifetime.TotalMinutes));
        Check(model, typeof(DataSubjectRequest), "ck_data_subject_requests_window").Should().Contain(string.Format(invariant,
            "execute_at = created_at + interval '{0} hours'", DataRequestLimits.CancellationWindow.TotalHours));
        Check(model, typeof(DataSubjectRequest), "ck_data_subject_requests_snapshot").Should().Contain(string.Format(invariant,
            "BETWEEN 1 AND {0}", DataRequestLimits.MaximumSnapshotMemberships));
        Check(model, typeof(DataSubjectRequest), "ck_data_subject_requests_progress").Should()
            .Contain(string.Format(invariant, "<= {0}", DataRequestLimits.MaximumParticipants))
            .And.Contain(string.Format(invariant, "{{0,{0}}}", DataRequestLimits.MaximumParticipantKeyLength - 1));
        Check(model, typeof(DataSubjectRequest), "ck_data_subject_requests_result").Should()
            .Contain(DataRequestLimits.MaximumTruncationCount.ToString(invariant));

        var chunks = Ddl("ChunkTable");
        chunks.Should().Contain(string.Format(invariant, "chunk_index >= 0 AND chunk_index < {0}", DataRequestLimits.MaximumChunkCount))
            .And.Contain(string.Format(invariant, "octet_length(nonce) = {0}", DataRequestLimits.NonceBytes))
            .And.Contain(string.Format(invariant, "octet_length(tag) = {0}", DataRequestLimits.TagBytes))
            .And.Contain(string.Format(invariant, "octet_length(ciphertext) BETWEEN 1 AND {0}", DataRequestLimits.ChunkPlaintextBytes));
    }

    [Theory]
    [InlineData(nameof(DataSubjectRequest), nameof(DataSubjectRequest.ResultJson))]
    [InlineData(nameof(DataExportArtifact), nameof(DataExportArtifact.ByteCount))]
    [InlineData(nameof(DataExportArtifact), nameof(DataExportArtifact.ChunkCount))]
    [InlineData(nameof(DataExportArtifact), nameof(DataExportArtifact.TruncationJson))]
    public void The_receipts_counts_and_sizes_are_audit_records_of_the_person(string entity, string property)
    {
        using var identity = Identity();
        var type = entity == nameof(DataSubjectRequest) ? typeof(DataSubjectRequest) : typeof(DataExportArtifact);

        identity.Model.FindEntityType(type)!.FindProperty(property)!.FindAnnotation(PersonalDataClassificationExtensions.CategoryAnnotation)!
            .Value.Should().Be(PersonalDataCategory.Audit,
                "how many of a person's records a group holds, and how large their bundle is, describe the person");
    }

    [Fact]
    public void The_platform_arm_precedes_the_application_arms_which_stay_byte_for_byte_those_of_Step_1()
    {
        const string End = "END $guard$;";
        const string ApplicationArms = "    IF (session_user <> 'learnstack_app'";
        var original = Ddl("RequestGuard");
        var replaced = Ddl("RequestGuard", "DataRequestOperatorDdl");
        original = original[..(original.IndexOf(End, StringComparison.Ordinal) + End.Length)];
        replaced = replaced[..(replaced.IndexOf(End, StringComparison.Ordinal) + End.Length)];

        replaced.Should().StartWith("CREATE OR REPLACE FUNCTION public.fn_identity_data_request_guard() RETURNS trigger");
        var header = original[..original.IndexOf(ApplicationArms, StringComparison.Ordinal)];
        var applicationArms = original[original.IndexOf(ApplicationArms, StringComparison.Ordinal)..];
        var platformArm = replaced[
            (header.Length + "OR REPLACE ".Length)..replaced.IndexOf(ApplicationArms, StringComparison.Ordinal)];
        replaced.Should().Be(header.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
            + platformArm + applicationArms,
            "the function's header and every application arm are Step 1's, character for character, after the platform arm");
        platformArm.Should().Contain("IF session_user = 'learnstack_platform' AND current_user = 'learnstack_platform' THEN")
            .And.Contain("OLD.kind = 'erasure' AND OLD.origin <> 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'")
            .And.Contain("NEW.status = 'completed'")
            .And.EndWith("        RETURN NEW;\n    END IF;\n", "the arm returns before the application's arms are reached");

        // The result may change only in the audit log's retained kinds (ADR-0107 § 6): each list the arm names is exactly
        // those four codes of the closed vocabulary.
        var auditKinds = new[]
        {
            RetainedReferenceKind.AuditActorReference, RetainedReferenceKind.AuditEntityReference,
            RetainedReferenceKind.AuditMetadataReference, RetainedReferenceKind.UnscopedAuditRows,
        }.Select(DataRequestCodes.RetainedKinds.ToCode).Order(StringComparer.Ordinal);
        var lists = System.Text.RegularExpressions.Regex.Matches(platformArm, @"ARRAY\[(?<codes>[^\]]+)\]");
        lists.Should().HaveCount(2);
        foreach (System.Text.RegularExpressions.Match list in lists)
        {
            list.Groups["codes"].Value.Split(',').Select(code => code.Trim().Trim('\'')).Order(StringComparer.Ordinal)
                .Should().Equal(auditKinds);
        }
    }

    /// <summary>
    /// Step 1's INSERT arm refused every request born <c>fulfilling</c> and read no User. Step 3 admits exactly one born
    /// <c>fulfilling</c>, a closure's fan-out erasure, and admits every request only for a subject in the status its origin
    /// allows (ADR-0107 § 7). This block is the whole of the change to the application arms.
    /// </summary>
    private const string Step1InitialState = """
                -- A request is never born fulfilling until a closure's fan-out has its own witness (ADR-0107 § 7): such a row
                -- would stand in for the erasure the profile clearing of a Revoked Membership requires, with no window.
                IF (NEW.row_version IS DISTINCT FROM 0 OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL
                    OR NEW.progress IS DISTINCT FROM '{}'::jsonb OR NEW.result IS DISTINCT FROM '{}'::jsonb
                    OR NEW.last_refusal IS NOT NULL OR NEW.failure_reason IS NOT NULL OR NEW.completed_at IS NOT NULL
                    OR NEW.status IS DISTINCT FROM (CASE WHEN NEW.kind = 'export' THEN 'requested'
                        WHEN NEW.origin = 'closure_fanout' THEN NULL ELSE 'scheduled' END)
                    OR NEW.created_by IS DISTINCT FROM COALESCE(NEW.requester_user_id, '00000000-0000-7000-8000-000000000001'::uuid)
                    OR NOT isfinite(NEW.created_at)
                    OR NEW.created_at > transaction_timestamp() + interval '5 minutes'
                    OR NEW.created_at < transaction_timestamp() - interval '5 minutes') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request initial state refused.' USING ERRCODE = '23514'; END IF;

        """;

    private const string ClosureInitialState = """
                -- A request is born fulfilling only as a closure's fan-out erasure, with no window and no requester
                -- (ADR-0107 § 7): such a row stands in for the erasure the profile clearing of a Revoked Membership requires.
                IF (NEW.row_version IS DISTINCT FROM 0 OR NEW.updated_at IS NOT NULL OR NEW.updated_by IS NOT NULL
                    OR NEW.progress IS DISTINCT FROM '{}'::jsonb OR NEW.result IS DISTINCT FROM '{}'::jsonb
                    OR NEW.last_refusal IS NOT NULL OR NEW.failure_reason IS NOT NULL OR NEW.completed_at IS NOT NULL
                    OR NEW.status IS DISTINCT FROM (CASE WHEN NEW.kind = 'export' THEN 'requested'
                        WHEN NEW.origin = 'closure_fanout' THEN 'fulfilling' ELSE 'scheduled' END)
                    OR (NEW.origin = 'closure_fanout' AND (NEW.kind IS DISTINCT FROM 'erasure' OR NEW.execute_at IS NOT NULL
                        OR NEW.requester_user_id IS NOT NULL))
                    OR NEW.created_by IS DISTINCT FROM COALESCE(NEW.requester_user_id, '00000000-0000-7000-8000-000000000001'::uuid)
                    OR NOT isfinite(NEW.created_at)
                    OR NEW.created_at > transaction_timestamp() + interval '5 minutes'
                    OR NEW.created_at < transaction_timestamp() - interval '5 minutes') IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request initial state refused.' USING ERRCODE = '23514'; END IF;
                -- The subject is admitted in the one status its origin allows, read here under the User fence held above:
                -- Closing for a closure's fan-out, Active for every other request (ADR-0107 § 7). A filing admitted while
                -- the person was Active and fenced only after a Begin or a Finalize committed inserts nothing.
                IF (public.fn_identity_user_status(NEW.subject_user_id)
                    IS DISTINCT FROM (CASE WHEN NEW.origin = 'closure_fanout' THEN 'Closing' ELSE 'Active' END)) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request subject is not admitted.' USING ERRCODE = '23514'; END IF;

        """;

    [Fact]
    public void The_closure_guard_keeps_Step_1_s_application_arms_but_for_the_one_INSERT_witness_block()
    {
        const string End = "END $guard$;";
        const string ApplicationArms = "    IF (session_user <> 'learnstack_app'";
        var original = Ddl("RequestGuard");
        var closure = Ddl("RequestGuard", "AccountClosureStorageDdl");
        original = original[..(original.IndexOf(End, StringComparison.Ordinal) + End.Length)];
        closure = closure[..(closure.IndexOf(End, StringComparison.Ordinal) + End.Length)];

        var header = original[..original.IndexOf(ApplicationArms, StringComparison.Ordinal)];
        var applicationArms = original[original.IndexOf(ApplicationArms, StringComparison.Ordinal)..];
        var before = Step1InitialState.ReplaceLineEndings("\n");
        var after = ClosureInitialState.ReplaceLineEndings("\n");
        applicationArms.IndexOf(before, StringComparison.Ordinal).Should().BeGreaterThan(0)
            .And.Be(applicationArms.LastIndexOf(before, StringComparison.Ordinal), "Step 1's initial-state block is replaced exactly once");
        closure.Should().StartWith("CREATE OR REPLACE FUNCTION public.fn_identity_data_request_guard() RETURNS trigger");
        var platformArm = closure[(header.Length + "OR REPLACE ".Length)..closure.IndexOf(ApplicationArms, StringComparison.Ordinal)];
        closure.Should().Be(header.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
            + platformArm + applicationArms.Replace(before, after, StringComparison.Ordinal),
            "the header and every application arm are Step 1's, character for character, but for the INSERT arm's one block");

        // The platform arm: the erasure completion of Step 2 and the closure's four arrows, then its return.
        platformArm.Should().Contain("IF session_user = 'learnstack_platform' AND current_user = 'learnstack_platform' THEN")
            .And.Contain("OLD.kind = 'erasure' AND OLD.origin <> 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'")
            .And.Contain("OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'scheduled'")
            .And.Contain("OLD.kind = 'closure' AND OLD.status = 'scheduled' AND NEW.status = 'fulfilling'")
            .And.Contain("OLD.kind = 'closure' AND OLD.status = 'fulfilling' AND NEW.status = 'completed'")
            .And.Contain("OLD.kind = 'erasure' AND OLD.origin = 'closure_fanout' AND OLD.status = 'awaiting_privileged_step'")
            .And.EndWith("        RETURN NEW;\n    END IF;\n", "the arm returns before the application's arms are reached");

        // The refusal record takes exactly the closed refusal codes.
        var refusals = System.Text.RegularExpressions.Regex.Match(platformArm, @"NEW\.last_refusal NOT IN \((?<codes>[^)]+)\)");
        refusals.Success.Should().BeTrue();
        refusals.Groups["codes"].Value.Split(',').Select(code => code.Trim().Trim('\'')).Order(StringComparer.Ordinal).Should()
            .Equal(Enum.GetValues<DataRequestRefusal>().Select(DataRequestCodes.Refusals.ToCode).Order(StringComparer.Ordinal));

        // The result changes only in the audit log's four retained kinds (ADR-0107 § 6), and a closure's Finalize also in
        // the unserved groups: three arrows each name their list twice, and only Finalize's names the unserved kind.
        var auditKinds = new[]
        {
            RetainedReferenceKind.AuditActorReference, RetainedReferenceKind.AuditEntityReference,
            RetainedReferenceKind.AuditMetadataReference, RetainedReferenceKind.UnscopedAuditRows,
        }.Select(DataRequestCodes.RetainedKinds.ToCode).Order(StringComparer.Ordinal).ToArray();
        var finalizeKinds = auditKinds.Append(DataRequestCodes.RetainedKinds.ToCode(RetainedReferenceKind.UnservedGroup))
            .Order(StringComparer.Ordinal).ToArray();
        var lists = System.Text.RegularExpressions.Regex.Matches(platformArm, @"ARRAY\[(?<codes>[^\]]+)\]")
            .Select(list => list.Groups["codes"].Value.Split(',').Select(code => code.Trim().Trim('\'')).Order(StringComparer.Ordinal).ToArray())
            .ToArray();
        lists.Should().HaveCount(6);
        lists.Count(list => list.SequenceEqual(auditKinds)).Should().Be(4);
        lists.Count(list => list.SequenceEqual(finalizeKinds)).Should().Be(2);

        // Begin and Finalize require the same participant fences as the users trigger exception: every tenant of a
        // Membership of any status, read on the writing connection.
        const string Participants = "GROUP BY member.tenant_id\n";
        const string Held = "HAVING public.fn_identity_lock_held('tenant', member.tenant_id, true) IS NOT TRUE";
        CountOf(platformArm, Participants).Should().Be(2);
        CountOf(platformArm, Held).Should().Be(2);
        var users = Ddl("UserGuard", "AccountClosureStorageDdl");
        CountOf(users, Participants).Should().Be(1);
        CountOf(users, Held).Should().Be(1);
    }

    /// <summary>
    /// A1's Finalize arm, as <c>20261123100000_admit_account_closure</c> applied it: a closure completes from
    /// <c>fulfilling</c> only.
    /// </summary>
    private const string BegunClosureCompletion = """
                ELSIF OLD.kind = 'closure' AND OLD.status = 'fulfilling' AND NEW.status = 'completed' THEN
                    -- A closure's Finalize (ADR-0107 § 7, ADR-0108 § 8): with the subject already Closed, under the fences
                    -- of Begin. The result records the redaction's four audit kinds and the unserved groups, nothing else.

        """;

    /// <summary>
    /// The same arm as <c>20261123120000_admit_closure_finalize</c> replaces it: a closure of the subject that was never
    /// begun completes too, under exactly the same witnesses, because the arm's body is unchanged.
    /// </summary>
    private const string EveryOpenClosureCompletion = """
                ELSIF OLD.kind = 'closure' AND OLD.status IN ('scheduled', 'fulfilling') AND NEW.status = 'completed' THEN
                    -- A closure's Finalize (ADR-0107 § 7, ADR-0108 § 8): with the subject already Closed, under the fences
                    -- of Begin, the closure it finalizes from fulfilling and every other closure of the subject from where
                    -- it stands, begun or not. The result records the redaction's four audit kinds and the unserved groups,
                    -- nothing else.

        """;

    [Fact]
    public void The_Finalize_guard_is_the_closure_guard_but_for_the_one_arm_condition_that_admits_a_never_begun_closure()
    {
        const string End = "END $guard$;";
        var closure = Ddl("RequestGuard", "AccountClosureStorageDdl");
        var finalize = Ddl("FinalizeRequestGuard", "AccountClosureStorageDdl");
        closure = closure[..(closure.IndexOf(End, StringComparison.Ordinal) + End.Length)];
        finalize = finalize[..(finalize.IndexOf(End, StringComparison.Ordinal) + End.Length)];
        var before = BegunClosureCompletion.ReplaceLineEndings("\n");
        var after = EveryOpenClosureCompletion.ReplaceLineEndings("\n");

        CountOf(closure, before).Should().Be(1, "A1's arm is replaced exactly once");
        finalize.Should().Be(closure.Replace(before, after, StringComparison.Ordinal),
            "every arm, every witness and the application's arms are A1's, character for character, but for the one condition");
        CountOf(finalize, "NEW.status = 'completed'").Should().Be(CountOf(closure, "NEW.status = 'completed'"),
            "no arm is added: the never-begun closure takes the Finalize arm's own witnesses");
        Ddl("FinalizeGrants", "AccountClosureStorageDdl").Trim().Should().Be(
            "GRANT SELECT (updated_at) ON public.data_subject_requests TO learnstack_platform;",
            "the platform role gains the request's update stamp, a begun closure's Begin moment, and nothing else");
    }

    /// <summary>The INSERT arm's status witness as the closure guard and its Finalize replacement place it, before the snapshot.</summary>
    private const string StatusBeforeSnapshot = """
                -- The subject is admitted in the one status its origin allows, read here under the User fence held above:
                -- Closing for a closure's fan-out, Active for every other request (ADR-0107 § 7). A filing admitted while
                -- the person was Active and fenced only after a Begin or a Finalize committed inserts nothing.
                IF (public.fn_identity_user_status(NEW.subject_user_id)
                    IS DISTINCT FROM (CASE WHEN NEW.origin = 'closure_fanout' THEN 'Closing' ELSE 'Active' END)) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request subject is not admitted.' USING ERRCODE = '23514'; END IF;

        """;

    private const string SnapshotEnd = """
                THEN RAISE EXCEPTION 'Data request snapshot refused.' USING ERRCODE = '23514'; END IF;

        """;

    /// <summary>The same witness as the hardening places it, after the snapshot, so no refusal message reveals a status.</summary>
    private const string StatusAfterSnapshot = """
                -- The subject is admitted in the one status its origin allows, read here under the User fence held above:
                -- Closing for a closure's fan-out, Active for every other request (ADR-0107 § 7). A filing admitted while
                -- the person was Active and fenced only after a Begin or a Finalize committed inserts nothing. It is asked
                -- after the snapshot, so a refused snapshot says nothing about the subject's status.
                IF (public.fn_identity_user_status(NEW.subject_user_id)
                    IS DISTINCT FROM (CASE WHEN NEW.origin = 'closure_fanout' THEN 'Closing' ELSE 'Active' END)) IS NOT FALSE
                THEN RAISE EXCEPTION 'Data request subject is not admitted.' USING ERRCODE = '23514'; END IF;

        """;

    private const string TerminalCheck = """
            IF (OLD.status IN ('completed','blocked','failed','cancelled')) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request is terminal.' USING ERRCODE = '23514'; END IF;

        """;

    /// <summary>The application UPDATE arm's one new refusal: a closure is the application's only while it is scheduled.</summary>
    private const string ClosureIsTheApplicationsOnlyWhileScheduled = """
            -- A closure is the application's only while it is scheduled (ADR-0107 § 7): the person cancels it, and once it
            -- leaves scheduled every write of its row is the platform's arrows, so its Begin moment, its result and its
            -- version change only as Finalize reads them.
            IF (OLD.kind = 'closure' AND NOT (OLD.status = 'scheduled' AND NEW.status IN ('scheduled','cancelled'))) IS NOT FALSE
            THEN RAISE EXCEPTION 'Data request transition refused.' USING ERRCODE = '23514'; END IF;

        """;

    /// <summary>
    /// <c>20261123130000_harden_closure_request_guard</c>: the guard Finalize installed but for exactly two changes to the
    /// application arms. The INSERT arm's status witness moves after the snapshot check, and the UPDATE arm gains one
    /// refusal right after the terminal check, so a closure the platform made <c>fulfilling</c> takes no application write.
    /// Every platform arrow, every other witness and every message is Finalize's, character for character.
    /// </summary>
    [Fact]
    public void The_hardened_guard_is_the_Finalize_guard_but_for_the_status_witness_s_place_and_the_closure_s_application_arm()
    {
        const string End = "END $guard$;";
        var finalize = Ddl("FinalizeRequestGuard", "AccountClosureStorageDdl");
        var hardened = Ddl("HardenedRequestGuard", "AccountClosureStorageDdl");
        finalize = finalize[..(finalize.IndexOf(End, StringComparison.Ordinal) + End.Length)];
        hardened = hardened[..(hardened.IndexOf(End, StringComparison.Ordinal) + End.Length)];
        var statusBefore = StatusBeforeSnapshot.ReplaceLineEndings("\n");
        var snapshotEnd = SnapshotEnd.ReplaceLineEndings("\n");
        var statusAfter = StatusAfterSnapshot.ReplaceLineEndings("\n");
        var terminal = TerminalCheck.ReplaceLineEndings("\n");
        var closureArm = ClosureIsTheApplicationsOnlyWhileScheduled.ReplaceLineEndings("\n");

        CountOf(finalize, statusBefore).Should().Be(1, "Finalize's guard asks the status once, before the snapshot");
        CountOf(finalize, snapshotEnd).Should().Be(1);
        CountOf(finalize, terminal).Should().Be(1);
        var expected = finalize.Replace(statusBefore, string.Empty, StringComparison.Ordinal)
            .Replace(snapshotEnd, snapshotEnd + statusAfter, StringComparison.Ordinal)
            .Replace(terminal, terminal + closureArm, StringComparison.Ordinal);
        hardened.Should().Be(expected,
            "every arm, witness and message is Finalize's, character for character, but for the witness's place and the one refusal");
        hardened.IndexOf("'Data request snapshot refused.'", StringComparison.Ordinal).Should().BeLessThan(
            hardened.IndexOf("'Data request subject is not admitted.'", StringComparison.Ordinal),
            "a refused snapshot is answered before the status is asked, so its message reveals no status");
        var platformArm = hardened[..hardened.IndexOf("    IF (session_user <> 'learnstack_app'", StringComparison.Ordinal)];
        platformArm.Should().NotContain(closureArm.Trim(), "the new refusal is the application's arm alone");
        DdlMethodSql("InstallHardening").Should().Equal([Ddl("HardenedRequestGuard", "AccountClosureStorageDdl")],
            "the hardening's migration replaces the guard and changes nothing else");
    }

    /// <summary>The statements an <c>AccountClosureStorageDdl</c> installer hands the migration builder, in order.</summary>
    private static List<string> DdlMethodSql(string method)
    {
        var builder = new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(IdentityDbContext).Assembly
            .GetType("LearnStack.Modules.Identity.Infrastructure.Persistence.AccountClosureStorageDdl", throwOnError: true)!
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [builder]);
        return [.. builder.Operations.Cast<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>().Select(operation => operation.Sql)];
    }

    private static int CountOf(string text, string value) =>
        System.Text.RegularExpressions.Regex.Count(text, System.Text.RegularExpressions.Regex.Escape(value));

    private static string Check(IModel model, Type entity, string name) =>
        model.FindEntityType(entity)!.GetCheckConstraints().SingleOrDefault(check => check.Name == name)?.Sql
        ?? throw new InvalidOperationException("The constraint is not in the model: " + name);

    private static string Ddl(string name, string type = "DataRequestStorageDdl") => (string)typeof(IdentityDbContext).Assembly
        .GetType("LearnStack.Modules.Identity.Infrastructure.Persistence." + type, throwOnError: true)!
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    private static IdentityDbContext Identity() => new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(ModelOnly).Options,
        StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
}
