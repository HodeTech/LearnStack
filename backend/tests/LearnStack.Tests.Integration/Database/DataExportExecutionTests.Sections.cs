using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 3, § 6 and ADR-0106 § 5 with real seeds through the actual job: what the Identity and Tenancy sections
/// hold for a person who issued, accepted and consented, the retained counts of each kind, and the job's signals.
/// </summary>
public sealed partial class DataExportExecutionTests
{
    [Fact]
    public async Task The_identity_and_tenancy_sections_and_the_retained_report_follow_the_subject_predicate()
    {
        await using var database = await OpenDatabaseAsync();
        var seeded = await SeedPersonAsync();
        var logs = new ExportLogs();
        await using var host = Host(services => services.AddSingleton<ILogger<DataExportJobHandlers>>(logs.Logger<DataExportJobHandlers>()));
        var request = await FileExportAsync(SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
        var measurements = new ConcurrentBag<(string Instrument, double Value, string? Outcome)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == DataExportTelemetry.MeterName)
                {
                    meter.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, Outcome(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, Outcome(tags))));
        listener.Start();

        var result = await RunAsync(host.Services, await SubmitAsync(SchemaFixture.TenantA, null, request));

        result.CommitAcknowledged.Should().BeTrue("disposition {0}, code {1}, {2}", result.Decision.Disposition, result.Decision.ErrorCode,
            string.Join(" | ", logs.Entries.Select(entry => entry.Message)));
        var stored = (await ArtifactsAsync(SchemaFixture.TenantA, null, PersonA)).Single(artifact => artifact.Request == request);
        var bytes = await ByteCountAsync(stored.Id);
        using var bundle = await DecryptAsync(host.Services, SchemaFixture.TenantA, null, stored.Id);
        var text = bundle.RootElement.GetRawText();
        var identity = bundle.RootElement.GetProperty("sections").GetProperty("identity");

        // Issued: the invitee's address is another person's data and never leaves; the acceptor is a party.
        var issued = identity.GetProperty("invitations_issued").EnumerateArray().ToList();
        issued.Should().HaveCount(3);
        issued.Where(record => record.TryGetProperty("recipient_email", out var _)).Should().BeEmpty();
        issued.Select(record => record.GetProperty("accepted_by").ValueKind == JsonValueKind.Null ? null : record.GetProperty("accepted_by").GetString())
            .Should().BeEquivalentTo(new string?[] { "third_party", "self", null });
        text.Should().NotContain(seeded.InviteeAccepted).And.NotContain(seeded.InviteePending);

        // Accepted: the person's own address, and who issued it as a party.
        var accepted = identity.GetProperty("invitations_accepted").EnumerateArray()
            .ToDictionary(record => record.GetProperty("recipient_email").GetString()!);
        accepted.Keys.Should().BeEquivalentTo([seeded.OfferAddress, seeded.SelfAddress]);
        accepted[seeded.OfferAddress].GetProperty("issued_by").GetString().Should().Be("third_party");
        accepted[seeded.SelfAddress].GetProperty("issued_by").GetString().Should().Be("self");

        // Custom fields: the stored value with its revision, labels and classification, although its consent was withdrawn.
        var field = identity.GetProperty("memberships").EnumerateArray().Single().GetProperty("profile").GetProperty("custom_fields")
            .EnumerateArray().Single();
        field.GetProperty("field").GetString().Should().Be("export-field");
        field.GetProperty("revision").GetInt32().Should().Be(1);
        field.GetProperty("labels").GetProperty("en").GetString().Should().Be("Export field");
        field.GetProperty("classification").GetString().Should().Be("sensitive");
        field.GetProperty("consent_recorded").GetBoolean().Should().BeTrue();
        field.GetProperty("value").GetProperty("value").GetBoolean().Should().BeTrue("a withdrawn consent withholds ordinary disclosure, not the person's own export");

        // Consents: the subject's, in this group, through their snapshot Membership; another person's and another
        // organization's rows stay out, and the actor is a closed word.
        var consents = bundle.RootElement.GetProperty("sections").GetProperty("tenancy").GetProperty("field_consents").EnumerateArray().ToList();
        consents.Select(record => record.GetProperty("state").GetString()).Should().Equal("granted", "withdrawn");
        consents.Should().OnlyContain(record => record.GetProperty("recorded_by").GetString() == "self"
            && record.GetProperty("purpose").GetString() == "export-purpose");
        consents.Select(record => record.GetProperty("sequence").GetInt64()).Should().Equal(1, 2);

        var retained = JsonDocument.Parse((await RequestAsync(SchemaFixture.TenantA, null, request)).Result).RootElement;
        long Count(string kind) => retained.GetProperty(kind).GetInt64();
        Count("consent_evidence").Should().Be(2, "the granted and the withdrawn act of the snapshot Membership");
        Count("invitation_record").Should().Be(4, "three issued (one also accepted by the subject) and one accepted from an administrator");
        Count("request_receipt").Should().Be(5, "two requests, their two artifacts and the seeded ticket");
        Count("attribution_reference").Should().BeGreaterThanOrEqualTo(4, "the three invitations the subject created and the offer they accepted");
        retained.TryGetProperty("federation_actor_reference", out _).Should().BeTrue("the tenant-wide group reports the tenant's federation rows");
        retained.TryGetProperty("provider_event_record", out _).Should().BeTrue();

        // Signals: one completed run with the stored size; a log line with the request, kind, sizes and duration and no person.
        measurements.Should().Contain((DataExportTelemetry.ExportCounterName, 1d, "completed"));
        measurements.Should().Contain((DataExportTelemetry.BundleBytesHistogramName, (double)bytes, null));
        measurements.Should().Contain(measurement => measurement.Instrument == DataExportTelemetry.DurationHistogramName
            && measurement.Outcome == "completed");
        var line = logs.Entries.Single(entry => entry.Id == 7501 && entry.Message.Contains(request.ToString(), StringComparison.Ordinal));
        line.Level.Should().Be(LogLevel.Information);
        line.Message.Should().Contain("(export)").And.Contain(bytes + " bytes").And.Contain(stored.ChunkCount + " chunks");
        logs.Entries.Should().OnlyContain(entry => !entry.Message.Contains(PersonA.ToString(), StringComparison.OrdinalIgnoreCase)
            && !entry.Message.Contains(AdminA.ToString(), StringComparison.OrdinalIgnoreCase)
            && !entry.Message.Contains("@example.invalid", StringComparison.Ordinal));
    }

    private async Task<long> ByteCountAsync(Guid artifact)
    {
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<long>(app, transaction, "SELECT byte_count FROM public.data_export_artifacts WHERE id=@id", ("id", artifact));
    }

    private static string? Outcome(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "outcome")
            {
                return tag.Value as string;
            }
        }

        return null;
    }

    private sealed record SeededPerson(string OfferAddress, string SelfAddress, string InviteeAccepted, string InviteePending);

    /// <summary>
    /// In tenant A's tenant-wide group: three invitations PersonA issued (one accepted by AdminA, one by PersonA, one
    /// pending), the seeded administrator offer accepted by PersonA, a custom-field value on PersonA's Membership whose
    /// consent was then withdrawn, and AdminA's own consent.
    /// </summary>
    private async Task<SeededPerson> SeedPersonAsync()
    {
        var acceptedByAdmin = Guid.CreateVersion7();
        var acceptedBySelf = Guid.CreateVersion7();
        var pending = Guid.CreateVersion7();
        var seeded = new SeededPerson("invitation-" + InvitationSchemaSeed.TenantOfferA.ToString("N") + "@example.invalid",
            "self-" + acceptedBySelf.ToString("N") + "@example.invalid", "admin-" + acceptedByAdmin.ToString("N") + "@example.invalid",
            "pending-" + pending.ToString("N") + "@example.invalid");
        await using var app = await OpenAppAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        foreach (var key in new[] { AdminA, PersonA }.Select(user => IdentityAdvisoryLockKey.ForUser(UserId.From(user))).Order())
        {
            await ExecuteAsync(app, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", key));
        }

        // Exclusive: the consent appends take the tenant fence exclusively, and the invitation and profile writes accept it.
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, true);
        foreach (var (id, email) in new[] { (acceptedByAdmin, seeded.InviteeAccepted), (acceptedBySelf, seeded.SelfAddress), (pending, seeded.InviteePending) })
        {
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.invitations(id,tenant_id,organization_id,recipient_email,inviter_user_id,role_id,
                    pinned_role_version,status,generation_id,generation,generation_created_at,expires_at,
                    verifier_digest,entry_origin,entry_locale,current_delivery_job_id,submission_status,created_at,created_by)
                VALUES(@id,@tenant,NULL,@email,@inviter,@role,0,'Pending',@generation,1,
                    now(),now()+interval '14 days',@digest,
                    'https://manage.invitation.invalid','en',@job,'Queued',now(),@inviter)
                """, ("id", id), ("tenant", SchemaFixture.TenantA), ("email", email), ("inviter", PersonA),
                ("role", RoleId(SchemaFixture.TenantA, LearnStack.SharedKernel.Permissions.BuiltInRoleKey.Learner,
                    LearnStack.SharedKernel.Permissions.PermissionScope.Tenant)),
                ("generation", Guid.CreateVersion7()), ("job", Guid.CreateVersion7()), ("digest", InvitationSchemaSeed.Digest(id)));
        }

        foreach (var (id, acceptor, membership) in new[]
                 {
                     (acceptedByAdmin, AdminA, AdminMembershipA), (acceptedBySelf, PersonA, LearnerMembershipA),
                     (InvitationSchemaSeed.TenantOfferA, PersonA, LearnerMembershipA),
                 })
        {
            await ExecuteAsync(app, transaction, """
                UPDATE public.invitations SET status='Accepted',consumed_verifier_digest=verifier_digest,verifier_digest=NULL,
                    accepted_by=@acceptor,result_membership_id=@member,acceptance_disposition='AlreadyMember',accepted_at=clock_timestamp(),
                    row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@acceptor WHERE id=@id
                """, ("acceptor", acceptor), ("member", membership), ("id", id));
        }

        // A Sensitive revision with its consent: granted, the value stored under it, then withdrawn; AdminA's own grant,
        // and PersonA's grant in an organization, which a tenant-wide export must not show.
        var definition = Guid.CreateVersion7();
        await ExecuteAsync(app, transaction, "SELECT pg_advisory_xact_lock(public.fn_identity_contextual_lock_key('tenant', @tenant))",
            ("tenant", SchemaFixture.TenantA));
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.tenant_custom_field_defs
                (id,tenant_id,target_entity,key,schema_version,status,json_schema,labels_json,pii_category,
                 is_required,sort,maximum_value_bytes,purpose_key,notices_json,created_at,created_by,row_version)
            VALUES(@definition,@tenant,'Membership','export-field',1,'Draft',
                '{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}',
                '{"en":"Export field"}','Sensitive',false,0,64,'export-purpose','{"en":"Export notice"}',clock_timestamp(),@actor,0);
            UPDATE public.tenant_custom_field_defs SET status='Active',row_version=1 WHERE id=@definition;
            """, ("definition", definition), ("tenant", SchemaFixture.TenantA), ("actor", SchemaFixture.Actor));
        var grant = Guid.CreateVersion7();
        foreach (var (id, organization, subject, membership, state, sequence, predecessor) in new (Guid, Guid?, Guid, Guid, string, int, Guid?)[]
                 {
                     (grant, null, PersonA, LearnerMembershipA, "Granted", 1, null),
                     (Guid.CreateVersion7(), SchemaFixture.OrgA1, PersonA, BranchMembershipA1, "Granted", 1, null),
                     (Guid.CreateVersion7(), null, AdminA, AdminMembershipA, "Granted", 1, null),
                 })
        {
            await AppendConsentAsync(app, transaction, id, organization, subject, membership, definition, state, sequence, predecessor);
        }

        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await ExecuteAsync(app, transaction, """
            UPDATE public.membership_profiles SET custom_fields=jsonb_build_array(jsonb_build_object('conceptKey','export-field',
                'definitionId',@definition::text,'schemaVersion',1,'envelope',jsonb_build_object('value',true),'consentEvidenceId',@grant::text))
            WHERE membership_id=@member
            """, ("definition", definition), ("grant", grant), ("member", LearnerMembershipA));
        await AppendConsentAsync(app, transaction, Guid.CreateVersion7(), null, PersonA, LearnerMembershipA, definition, "Withdrawn", 2, grant);
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await transaction.CommitAsync();
        return seeded;
    }

    private static async Task AppendConsentAsync(NpgsqlConnection app, NpgsqlTransaction transaction, Guid id, Guid? organization,
        Guid subject, Guid membership, Guid definition, string state, int sequence, Guid? predecessor)
    {
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.membership_field_consents
                (id,tenant_id,organization_id,membership_id,subject_user_id,actor_user_id,definition_id,
                 definition_version,purpose_key,notice_locale,notice_hash,sequence,state,predecessor_id,predecessor_sequence,
                 created_at,created_by,row_version)
            VALUES(@id,@tenant,@organization,@membership,@subject,@subject,@definition,1,'export-purpose',
                'en',encode(sha256(convert_to('Export notice','UTF8')),'hex'),@sequence,@state,@predecessor,@sequence - 1,
                clock_timestamp(),@subject,0)
            """, ("id", id), ("tenant", SchemaFixture.TenantA), ("organization", organization), ("membership", membership),
            ("subject", subject), ("definition", definition), ("sequence", sequence), ("state", state),
            ("predecessor", (object?)predecessor ?? DBNull.Value));
    }

    /// <summary>The export job's formatted lines, by event identifier and level.</summary>
    private sealed class ExportLogs
    {
        private readonly ConcurrentQueue<(int Id, LogLevel Level, string Message)> _entries = new();

        public IReadOnlyCollection<(int Id, LogLevel Level, string Message)> Entries => [.. _entries];

        public ILogger<T> Logger<T>() => new Capture<T>(_entries);

        private sealed class Capture<T>(ConcurrentQueue<(int Id, LogLevel Level, string Message)> entries) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue((eventId.Id, logLevel, formatter(state, exception)));
        }
    }
}
