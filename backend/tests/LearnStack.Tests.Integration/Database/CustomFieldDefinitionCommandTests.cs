using System.Text.Json;
using System.Globalization;
using System.Text;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.Validation;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Audit;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Application.CustomFields;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Validation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class CustomFieldDefinitionCommandTests(CustomFieldCommandFixture fixture) : IClassFixture<CustomFieldCommandFixture>
{
    private const string Schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}""";

    private static CustomFieldDefinitionInput Input(string key, int version = 1) => new(
        "Membership", key, version, Schema, new Dictionary<string, string> { ["en"] = "Authored label canary" },
        PersonalDataCategory.None, false, 0, null, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admitted_schemas_publish_after_PostgreSQL_expands_stored_whitespace_or_exponents(bool numericExpansion)
    {
        var choices = Enumerable.Range(0, 20).Select(index => index.ToString("D2", CultureInfo.InvariantCulture)
            + new string('x', 87)).ToArray();
        var schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"string","minLength":1,"maxLength":100,"enum":""" + JsonSerializer.Serialize(choices) + """}},"required":["value"],"additionalProperties":false}""";
        if (numericExpansion)
        {
            var properties = string.Join(',', Enumerable.Range(0, 10).Select(index =>
                JsonSerializer.Serialize("field-" + index.ToString(CultureInfo.InvariantCulture))
                + """:{"type":"number","minimum":1e-300,"maximum":1e-299}"""));
            schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"object","properties":{""" + properties + """},"required":[],"additionalProperties":false}},"required":["value"],"additionalProperties":false}""";
        }
        var key = "jsonb-" + Guid.NewGuid().ToString("N");
        var input = Input(key) with
        {
            JsonSchema = schema,
            FormLabels = numericExpansion ? Enumerable.Range(0, 10).Select(index => new CustomFieldControlLabels(
                "/" + index.ToString(CultureInfo.InvariantCulture), new Dictionary<string, string> { ["en"] = "Field " + index.ToString(CultureInfo.InvariantCulture) })).ToArray()
                : [new("/", null, choices.Select((_, index) =>
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["en"] = "Choice " + index.ToString(CultureInfo.InvariantCulture) }).ToArray())],
        };
        var created = await fixture.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), input), "field-jsonb-register");
        Assert.True(created.IsSuccess);
        Assert.True(Encoding.UTF8.GetByteCount(created.Value.JsonSchema) <= CustomFieldFormProfile.SchemaByteLimit);
        if (!numericExpansion) Assert.Equal(2044, Encoding.UTF8.GetByteCount(created.Value.JsonSchema));
        var stored = await fixture.SendAsync(new GetCustomFieldDefinitionQuery(created.Value.DefinitionId), "field-jsonb-read");
        Assert.True(stored.IsSuccess);
        Assert.True(Encoding.UTF8.GetByteCount(stored.Value.JsonSchema) > CustomFieldFormProfile.SchemaByteLimit);
        var published = await fixture.SendAsync(new PublishCustomFieldDefinitionCommand(created.Value.DefinitionId,
            created.Value.Version, null, null), "field-jsonb-publish");
        Assert.True(published.IsSuccess);
        Assert.Equal("Active", published.Value.Status);
    }

    [Fact]
    public async Task Discard_preserves_the_permanent_revision_reservation_in_actual_EF_reads()
    {
        var key = "discard-" + Guid.NewGuid().ToString("N");
        var draft = await fixture.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), Input(key)), "field-discard-register");
        Assert.True(draft.IsSuccess);
        var discarded = await fixture.SendAsync(new DiscardCustomFieldDefinitionDraftCommand(draft.Value.DefinitionId, draft.Value.Version), "field-discard");
        Assert.True(discarded.IsSuccess);
        Assert.True(discarded.Value.IsDiscarded);
        var reused = await fixture.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), Input(key)), "field-discard-reuse");
        Assert.Equal("business_rule_violation", reused.Error!.Code);
        var successor = await fixture.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), Input(key, 2)), "field-discard-successor");
        Assert.True(successor.IsSuccess);
        var history = await fixture.SendAsync(new ListCustomFieldDefinitionRevisionsQuery(successor.Value.DefinitionId), "field-discard-history");
        Assert.True(history.IsSuccess);
        Assert.Contains(history.Value.Items, row => row.DefinitionId == draft.Value.DefinitionId && row.IsDiscarded);
    }

    [Fact]
    public async Task Publication_atomically_retires_the_expected_incumbent_and_audits_the_designated_successor()
    {
        var key = "field-" + Guid.NewGuid().ToString("N");
        var first = await fixture.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), Input(key)), "field-register-first");
        Assert.True(first.IsSuccess);
        var active = await fixture.SendAsync(new PublishCustomFieldDefinitionCommand(first.Value.DefinitionId, first.Value.Version, null, null), "field-first-publish");
        Assert.True(active.IsSuccess);
        var second = await fixture.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), Input(key, 2)), "field-register-second");
        Assert.True(second.IsSuccess);
        var conflict = await fixture.SendAsync(new PublishCustomFieldDefinitionCommand(second.Value.DefinitionId, second.Value.Version, null, null), "field-publication-stale-absence");
        Assert.Equal("concurrency_conflict", conflict.Error!.Code);
        var published = await fixture.SendAsync(new PublishCustomFieldDefinitionCommand(second.Value.DefinitionId, second.Value.Version,
            active.Value.DefinitionId, active.Value.Version), "field-successor-publish");
        Assert.True(published.IsSuccess);
        Assert.Equal("Active", published.Value.Status);
        var prior = await fixture.SendAsync(new GetCustomFieldDefinitionQuery(first.Value.DefinitionId), "field-retained-history");
        Assert.Equal("Deprecated", prior.Value!.Status);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(first.Value.JsonSchema),
            JsonSerializer.Deserialize<JsonElement>(prior.Value.JsonSchema)));
        using var audit = JsonDocument.Parse(Assert.Single(await fixture.Membership.AuditAsync("field-successor-publish", "customization.custom_field.publish")));
        Assert.Equal("success", audit.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Must", audit.RootElement.GetProperty("class").GetString());
        Assert.Equal("TenantCustomFieldDef", audit.RootElement.GetProperty("entity").GetString());
        Assert.Equal(second.Value.DefinitionId.ToString("D"), audit.RootElement.GetProperty("id").GetString());
        Assert.DoesNotContain("Authored label canary", audit.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MUST_audit_failure_rolls_back_the_definition_and_never_publishes_a_result()
    {
        var id = Guid.CreateVersion7();
        const string Correlation = "field-audit-refused";
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION fn_custom_field_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.operation='customization.custom_field.register' AND NEW.correlation_id='field-audit-refused' THEN
                    RAISE EXCEPTION 'fixed custom field audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_custom_field_audit_proof BEFORE INSERT ON audit_log
                FOR EACH ROW EXECUTE FUNCTION fn_custom_field_audit_proof();
            """);
        try
        {
            Result<CustomFieldDefinitionDto>? published = null;
            var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published =
                await fixture.SendAsync(new RegisterCustomFieldDefinitionCommand(id, Input("audit-" + id.ToString("N"))), Correlation));
            Assert.Equal("audit_unavailable", failure.Error!.Code);
            Assert.Null(published);
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.AppConnectionString);
            await using var tx = await app.BeginTransactionAsync();
            await ScopeAsync(app, tx, SchemaFixture.TenantA);
            Assert.Equal(0L, await ScalarAsync<long>(app, tx, "SELECT count(*) FROM tenant_custom_field_defs WHERE id=@id", ("id", id)));
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP TRIGGER tg_custom_field_audit_proof ON audit_log; DROP FUNCTION fn_custom_field_audit_proof()");
        }
    }

    [Fact]
    public async Task An_ordinary_member_and_branch_writer_cannot_author_definitions()
    {
        var request = new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), Input("denied-" + Guid.NewGuid().ToString("N")));
        Assert.Equal("forbidden", (await fixture.SendAsync(request, "field-denied-member", PersonA)).Error!.Code);
        Assert.Equal("forbidden", (await fixture.SendAsync(request, "field-denied-branch", organization: SchemaFixture.OrgA1)).Error!.Code);
    }
}

public sealed class CustomFieldCommandFixture : IAsyncLifetime
{
    public MembershipAdministrationFixture Membership { get; } = new();

    public async Task InitializeAsync()
    {
        await Membership.InitializeAsync();
        var version = 0L;
        foreach (var release in new[] { IdentityRoleRelease.Version2, IdentityRoleRelease.Version3 })
        {
            var adopted = await SendAsync(new AdoptRoleGrantManifestCommand(
                RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant), version,
                release.Version, release.Fingerprint), "custom-field-fixture-adopt-" + release.Version);
            Assert.True(adopted.IsSuccess);
            version = adopted.Value;
        }
    }

    public Task DisposeAsync() => Membership.DisposeAsync();

    internal Task<RoleGrantPipelineHarness> CreateAsync(string correlation, Guid? actor = null, Guid? organization = null, ILoggerFactory? loggers = null) =>
        RoleGrantPipelineHarness.CreateAsync(Membership.AppConnectionString, Authority, TenantId.From(SchemaFixture.TenantA),
            UserId.From(actor ?? AdminA), correlation, organization is { } org ? OrganizationId.From(org) : null,
            services =>
            {
                if (loggers is not null) services.AddSingleton(loggers);
                services.AddModuleDbContext<CustomizationDbContext>();
                services.AddModuleDbContext<TenancyDbContext>();
                services.AddScoped<ICustomFieldDefinitionStore, CustomFieldDefinitionStore>();
                services.AddScoped<CustomFieldDefinitionAdmission>();
                services.AddScoped<CustomFieldFormProfile>();
                services.AddScoped<ICustomFieldLocaleConfigurationReader, CustomFieldLocaleConfigurationReader>();
                services.AddSingleton<IJsonSchemaValidator, JsonSchemaNetValidator>();
                services.AddSingleton<IOperationPolicySource, CustomizationOperationPolicySource>();
                services.AddSingleton<IAuditCatalogSource, CustomizationAuditCatalogSource>();
                services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(CustomFieldDefinitionCommandHandlers).Assembly));
            });

    public async Task<Result<T>> SendAsync<T>(IRequest<Result<T>> request, string correlation, Guid? actor = null, Guid? organization = null)
    {
        await using var harness = await CreateAsync(correlation, actor, organization);
        return await harness.Sender.Send(request);
    }
}
