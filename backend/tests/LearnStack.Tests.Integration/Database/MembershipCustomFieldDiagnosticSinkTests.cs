using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;
using static LearnStack.Tests.Integration.Database.MembershipCustomFieldFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(IdentityDiagnosticSinkGroup.Name)]
public sealed class MembershipCustomFieldDiagnosticSinkTests(MembershipCustomFieldFixture fixture)
    : IClassFixture<MembershipCustomFieldFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task None_and_sensitive_values_stay_out_of_real_sinks_for_immediate_and_deferred_faults(bool deferred)
    {
        const string noneCanary = "none-value-canary-3462";
        const string sensitiveCanary = "sensitive-value-canary-7829";
        var correlation = "field-value-diagnostic-success-" + (deferred ? "deferred" : "immediate");
        const string schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"string","minLength":1,"maxLength":100}},"required":["value"],"additionalProperties":false}""";
        var member = await fixture.Membership.AddMemberAsync();
        var none = await fixture.PublishAsync(Input("diag-none-" + Guid.NewGuid().ToString("N")) with { JsonSchema = schema });
        var sensitive = await fixture.PublishAsync(Input("diag-sensitive-" + Guid.NewGuid().ToString("N"),
            category: PersonalDataCategory.Sensitive) with
        { JsonSchema = schema });
        var grant = await fixture.SendAsync(new GrantOwnMembershipFieldConsentCommand(sensitive.DefinitionId,
            sensitive.SchemaVersion, "en", MembershipFieldConsentNotice.ComputeHash(sensitive.Notices!["en"]), 0, null, true),
            "field-value-diagnostic-grant", member.Actor);
        grant.IsSuccess.Should().BeTrue();
        MembershipCustomFieldSet SetText(CustomFieldDefinitionDto definition, string value, Guid? evidence = null) =>
            new(definition.Key, definition.DefinitionId, definition.SchemaVersion,
                JsonSerializer.SerializeToElement(new { value }), evidence);
        var evidenceId = grant.Value!.EvidenceId;
        var saved = await fixture.SendAsync(new SetMembershipCustomFieldsCommand(member.Membership, 0,
            new([SetText(none, noneCanary), SetText(sensitive, sensitiveCanary, evidenceId)], [])), correlation);
        saved.IsSuccess.Should().BeTrue();
        var disclosure = await fixture.SendAsync(new GetMembershipCustomFieldsQuery(member.Membership), "field-value-diagnostic-disclosure");
        disclosure.IsSuccess.Should().BeTrue();
        disclosure.Value!.Fields.Single(item => item.DefinitionId == none.DefinitionId).Envelope!.Value.GetProperty("value").GetString().Should().Be(noneCanary);
        disclosure.Value.Fields.Single(item => item.DefinitionId == sensitive.DefinitionId).Envelope!.Value.GetProperty("value").GetString().Should().Be(sensitiveCanary);
        var audit = Assert.Single(await fixture.Membership.AuditAsync(correlation, "identity.membership.custom_fields_write"));
        audit.Should().NotBeNullOrWhiteSpace().And.NotContain(noneCanary).And.NotContain(sensitiveCanary);

        using var sinks = new IdentityDiagnosticSinks();
        using var source = new ActivitySource("learnstack.custom-field.value-proof");
        using var parent = source.StartActivity("custom-field-value-parent");
        parent.Should().NotBeNull();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.OwnerConnectionString);
        try
        {
            await ExecuteAsync(owner, null, """
                CREATE SEQUENCE public.custom_value_sink_source;
                GRANT USAGE, SELECT ON public.custom_value_sink_source TO learnstack_app;
                """);
            if (deferred)
            {
                // Graph validation has already forced its constraints immediate before
                // the audit write. Only this failure's audit insert re-defers this
                // test constraint, whose callback reads the actual pending profile.
                await ExecuteAsync(owner, null, """
                    CREATE FUNCTION public.custom_value_sink_arm() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        IF NEW.correlation_id = 'field-value-diagnostic-failure' THEN
                            SET CONSTRAINTS custom_value_sink_failure DEFERRED;
                        END IF;
                        RETURN NEW;
                    END $$;
                    CREATE FUNCTION public.custom_value_sink_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                    DECLARE payload text;
                    BEGIN
                        SELECT custom_fields::text INTO payload FROM public.membership_profiles
                        WHERE membership_id = NEW.entity_id::uuid AND tenant_id = NEW.tenant_id;
                        IF payload LIKE '%none-value-canary-3462%' AND payload LIKE '%sensitive-value-canary-7829%' THEN
                            PERFORM nextval('public.custom_value_sink_source');
                            RAISE EXCEPTION USING ERRCODE='P0399', MESSAGE=payload;
                        END IF;
                        RAISE EXCEPTION 'expected custom value payload was absent';
                    END $$;
                    CREATE CONSTRAINT TRIGGER custom_value_sink_failure AFTER INSERT ON public.audit_log
                        DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW
                        WHEN (NEW.correlation_id = 'field-value-diagnostic-failure')
                        EXECUTE FUNCTION public.custom_value_sink_failure();
                    CREATE TRIGGER custom_value_sink_arm BEFORE INSERT ON public.audit_log
                        FOR EACH ROW EXECUTE FUNCTION public.custom_value_sink_arm();
                    """);
            }
            else
            {
                await ExecuteAsync(owner, null, """
                    CREATE FUNCTION public.custom_value_sink_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        IF NEW.custom_fields::text LIKE '%none-value-canary-3462%' AND NEW.custom_fields::text LIKE '%sensitive-value-canary-7829%' THEN
                            PERFORM nextval('public.custom_value_sink_source');
                            RAISE EXCEPTION USING ERRCODE='P0399', MESSAGE=NEW.custom_fields::text;
                        END IF;
                        RETURN NEW;
                    END $$;
                    CREATE TRIGGER custom_value_sink_failure BEFORE UPDATE ON public.membership_profiles
                        FOR EACH ROW EXECUTE FUNCTION public.custom_value_sink_failure();
                    """);
            }
            await using var harness = await fixture.CreateAsync("field-value-diagnostic-failure", loggers: sinks.Loggers);
            var failure = await Record.ExceptionAsync(() => harness.Sender.Send(new SetMembershipCustomFieldsCommand(member.Membership, 1,
                new([SetText(none, noneCanary + "-changed"), SetText(sensitive, sensitiveCanary + "-changed", evidenceId)], []))));
            if (deferred) failure.Should().BeOfType<ConfidentialCommitProviderException>();
            else failure.Should().BeOfType<IdentityStorageProviderException>();
            failure!.InnerException.Should().BeNull();
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.AppConnectionString);
            (await ScalarAsync<bool>(observer, null, "SELECT is_called FROM public.custom_value_sink_source")).Should().BeTrue(
                "the actual database fault received both classified values");
            parent!.Stop();
            await sinks.CaptureThroughL1Async(failure);
            var captured = await sinks.ReadAsync();
            captured.AssertPositiveControls();
            captured.AssertExcludes([noneCanary, sensitiveCanary]);
        }
        finally
        {
            await ExecuteAsync(owner, null, """
                DROP TRIGGER IF EXISTS custom_value_sink_failure ON public.membership_profiles;
                DROP TRIGGER IF EXISTS custom_value_sink_failure ON public.audit_log;
                DROP TRIGGER IF EXISTS custom_value_sink_arm ON public.audit_log;
                DROP FUNCTION IF EXISTS public.custom_value_sink_failure();
                DROP FUNCTION IF EXISTS public.custom_value_sink_arm();
                DROP SEQUENCE IF EXISTS public.custom_value_sink_source;
                """);
        }
    }
}
