using System.Text.Json;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Infrastructure.Validation;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Audit;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Application.CustomFields;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.CustomFields;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.FieldConsents;
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

/// <summary>Real app-role commands, immutable definition reads and latest durable consent reads.</summary>
public sealed class MembershipCustomFieldFixture : IAsyncLifetime
{
    public CustomFieldCommandFixture Definitions { get; } = new();
    public MembershipAdministrationFixture Membership => Definitions.Membership;
    public Task InitializeAsync() => Definitions.InitializeAsync();
    public Task DisposeAsync() => Definitions.DisposeAsync();

    internal Task<RoleGrantPipelineHarness> CreateAsync(string correlation, Guid? actor = null, Guid? organization = null,
        Guid? tenant = null, ILoggerFactory? loggers = null) => RoleGrantPipelineHarness.CreateAsync(Membership.AppConnectionString, Authority,
        TenantId.From(tenant ?? SchemaFixture.TenantA), UserId.From(actor ?? AdminA), correlation,
        organization is { } org ? OrganizationId.From(org) : null, services =>
        {
            if (loggers is not null) services.AddSingleton(loggers);
            services.AddModuleDbContext<CustomizationDbContext>();
            services.AddModuleDbContext<TenancyDbContext>();
            services.AddScoped<ICustomFieldDefinitionStore, CustomFieldDefinitionStore>();
            services.AddScoped<CustomFieldDefinitionAdmission>();
            services.AddScoped<CustomFieldFormProfile>();
            services.AddScoped<ICustomFieldValueValidator>(provider => provider.GetRequiredService<CustomFieldFormProfile>());
            services.AddScoped<IExactCustomFieldDefinitionReader, ExactCustomFieldDefinitionReader>();
            services.AddScoped<ICustomFieldLocaleConfigurationReader, CustomFieldLocaleConfigurationReader>();
            services.AddScoped<ITenantLocaleEligibilityReader, TenantLocaleEligibilityReader>();
            services.AddScoped<IMembershipFieldConsentReader, MembershipFieldConsentReader>();
            services.AddScoped<IMembershipFieldConsentWriteStore, MembershipFieldConsentWriteStore>();
            services.AddScoped<IMembershipCustomFieldStore, MembershipCustomFieldStore>();
            services.AddScoped<ICustomFieldConsentMembershipReader, CustomFieldConsentMembershipReader>();
            services.AddScoped<MembershipCustomFieldService>();
            services.AddScoped<MembershipCustomFieldCommandExecutor>();
            services.AddScoped<MembershipCustomFieldQueryExecutor>();
            services.AddScoped<OwnFieldConsentAuthorization>();
            services.AddSingleton<IJsonSchemaValidator, JsonSchemaNetValidator>();
            services.AddSingleton<IOperationPolicySource, CustomizationOperationPolicySource>();
            services.AddSingleton<IAuditCatalogSource, CustomizationAuditCatalogSource>();
            services.AddSingleton<IAuditCatalogSource, TenancyAuditCatalogSource>();
            services.AddMediatR(configuration => configuration.RegisterServicesFromAssemblies(
                typeof(CustomFieldDefinitionCommandHandlers).Assembly, typeof(GrantOwnMembershipFieldConsentCommandHandler).Assembly));
        });

    public async Task<Result<T>> SendAsync<T>(IRequest<Result<T>> request, string correlation, Guid? actor = null,
        Guid? organization = null, Guid? tenant = null)
    {
        await using var harness = await CreateAsync(correlation, actor, organization, tenant);
        return await harness.Sender.Send(request);
    }

    public static CustomFieldDefinitionInput Input(string key, bool required = false,
        PersonalDataCategory category = PersonalDataCategory.None, int version = 1) => new(
        "Membership", key, version,
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}""",
        new Dictionary<string, string> { ["en"] = "Custom field label" }, category, required, 0,
        category == PersonalDataCategory.Sensitive ? "personal-purpose" : null,
        category == PersonalDataCategory.Sensitive ? new Dictionary<string, string> { ["en"] = "Sensitive collection notice" } : null);

    public async Task<CustomFieldDefinitionDto> PublishAsync(CustomFieldDefinitionInput input,
        CustomFieldDefinitionDto? incumbent = null)
    {
        var created = await Definitions.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(), input), "value-definition-register");
        Assert.True(created.IsSuccess, created.Error?.Code);
        var published = await Definitions.SendAsync(new PublishCustomFieldDefinitionCommand(created.Value.DefinitionId,
            created.Value.Version, incumbent?.DefinitionId, incumbent?.Version), "value-definition-publish");
        Assert.True(published.IsSuccess, published.Error?.Code);
        return published.Value;
    }

    public async Task RetireAsync(CustomFieldDefinitionDto definition)
    {
        var retired = await Definitions.SendAsync(new RetireCustomFieldDefinitionCommand(definition.DefinitionId, definition.Version), "value-definition-retire");
        Assert.True(retired.IsSuccess, retired.Error?.Code);
    }

    public static MembershipCustomFieldSet Set(CustomFieldDefinitionDto definition, bool value = true, Guid? evidence = null) =>
        new(definition.Key, definition.DefinitionId, definition.SchemaVersion,
            JsonSerializer.Deserialize<JsonElement>(value ? "{\"value\":true}" : "{\"value\":false}"), evidence);

    public static MembershipCustomFieldRemove Remove(CustomFieldDefinitionDto definition) =>
        new(definition.Key, definition.DefinitionId, definition.SchemaVersion);

    public async Task<string> StoredAsync(Guid member, Guid? organization = null)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Membership.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
        return await ScalarAsync<string>(app, transaction,
            "SELECT custom_fields::text FROM membership_profiles WHERE membership_id=@member", ("member", member));
    }

    // Evidence setup deliberately uses the same durable app-role append as storage
    // proofs; consent-entry authorization has its separate invocation/witness suite.
    public Task AppendWithdrawalAsync(Guid member, Guid actor, CustomFieldDefinitionDto definition, Guid grant) =>
        Membership.MutateAsync(SchemaFixture.TenantA, null, null, true,
            (app, transaction) => AppendWithdrawalAsync(app, transaction, member, actor, definition, grant));

    internal static Task AppendWithdrawalAsync(NpgsqlConnection app, NpgsqlTransaction transaction,
        Guid member, Guid actor, CustomFieldDefinitionDto definition, Guid grant) => ExecuteAsync(app, transaction, """
            INSERT INTO membership_field_consents
              (id,tenant_id,organization_id,membership_id,subject_user_id,actor_user_id,definition_id,definition_version,
               purpose_key,notice_locale,notice_hash,sequence,state,predecessor_id,predecessor_sequence,created_at,created_by,row_version)
            VALUES(@id,@tenant,NULL,@member,@actor,@actor,@definition,@version,@purpose,'en',@hash,
               2,'Withdrawn',@grant,1,clock_timestamp(),@actor,0)
            """, ("id", Guid.CreateVersion7()), ("tenant", SchemaFixture.TenantA), ("member", member), ("actor", actor),
            ("definition", definition.DefinitionId), ("version", definition.SchemaVersion), ("purpose", definition.PurposeKey),
            ("hash", MembershipFieldConsentNotice.ComputeHash(definition.Notices!["en"])), ("grant", grant));
}
