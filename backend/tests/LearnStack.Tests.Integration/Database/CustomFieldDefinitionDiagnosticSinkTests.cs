using System.Diagnostics;
using FluentAssertions;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Errors;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(IdentityDiagnosticSinkGroup.Name)]
public sealed class CustomFieldDefinitionDiagnosticSinkTests(CustomFieldCommandFixture fixture)
    : IClassFixture<CustomFieldCommandFixture>
{
    [Fact]
    public async Task Deferred_commit_payload_is_excluded_from_real_sinks_and_success_audit()
    {
        const string schemaCanary = "schema-canary-6842";
        const string labelCanary = "label-canary-8573";
        const string noticeCanary = "notice-canary-9234";
        const string correlation = "field-diagnostic-success";
        var schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"object","properties":{"schema-canary-6842":{"type":"boolean"}},"required":["schema-canary-6842"],"additionalProperties":false}},"required":["value"],"additionalProperties":false}""";
        CustomFieldDefinitionInput Input(string key) => new("Membership", key, 1, schema,
            new Dictionary<string, string> { ["en"] = labelCanary }, PersonalDataCategory.Sensitive, false, 0, "personal-purpose", new Dictionary<string, string> { ["en"] = noticeCanary },
            [new("/0", new Dictionary<string, string> { ["en"] = labelCanary })]);
        using var sinks = new IdentityDiagnosticSinks();
        using var activitySource = new ActivitySource("learnstack.custom-field.diagnostic-proof");
        using var activity = activitySource.StartActivity("custom-field-diagnostic-parent");
        activity.Should().NotBeNull();

        var success = await fixture.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(),
            Input("diagnostic-success-" + Guid.NewGuid().ToString("N"))), correlation);
        success.IsSuccess.Should().BeTrue();
        success.Value!.JsonSchema.Should().Contain(schemaCanary, "authorized successful disclosure remains available");
        var audit = Assert.Single(await fixture.Membership.AuditAsync(correlation, "customization.custom_field.register"));
        audit.Should().NotBeNullOrWhiteSpace().And.NotContain(schemaCanary).And.NotContain(labelCanary).And.NotContain(noticeCanary);

        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE SEQUENCE public.custom_field_sink_source;
            GRANT USAGE, SELECT ON public.custom_field_sink_source TO learnstack_app;
            CREATE FUNCTION public.custom_field_sink_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.json_schema::text LIKE '%schema-canary-6842%' AND NEW.labels_json::text LIKE '%label-canary-8573%'
                   AND NEW.notices_json::text LIKE '%notice-canary-9234%' THEN
                    PERFORM nextval('public.custom_field_sink_source');
                    RAISE EXCEPTION USING ERRCODE='P0399', MESSAGE=NEW.json_schema::text || NEW.labels_json::text || NEW.notices_json::text;
                END IF;
                RETURN NEW;
            END $$;
            CREATE CONSTRAINT TRIGGER custom_field_sink_failure AFTER INSERT ON public.tenant_custom_field_defs
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.custom_field_sink_failure();
            """);
        try
        {
            await using var harness = await fixture.CreateAsync("field-diagnostic-failure", loggers: sinks.Loggers);
            var failure = await Record.ExceptionAsync(() => harness.Sender.Send(new RegisterCustomFieldDefinitionCommand(
                Guid.CreateVersion7(), Input("diagnostic-failure-" + Guid.NewGuid().ToString("N")))));
            failure.Should().BeOfType<ConfidentialCommitProviderException>();
            failure!.InnerException.Should().BeNull();
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.AppConnectionString);
            (await ScalarAsync<bool>(observer, null, "SELECT is_called FROM public.custom_field_sink_source")).Should().BeTrue(
                "the deferred trigger saw all three authored values at the actual commit");
            activity!.Stop();
            await sinks.CaptureThroughL1Async(failure);
            var captured = await sinks.ReadAsync();
            captured.AssertPositiveControls();
            captured.AssertExcludes([schemaCanary, labelCanary, noticeCanary]);
        }
        finally
        {
            await ExecuteAsync(owner, null, """
                DROP TRIGGER custom_field_sink_failure ON public.tenant_custom_field_defs;
                DROP FUNCTION public.custom_field_sink_failure();
                DROP SEQUENCE public.custom_field_sink_source;
                """);
        }
    }
}
