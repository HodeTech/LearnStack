using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Exact fixture declarations; execution still requires the production job request owner.</summary>
internal static class BackgroundJobFixtureOperations
{
    internal const string OrganizationOperation = "jobfixture.organization.create";

    internal static OperationPolicy Trusted(Type type, AuthorizationSourceKind kind) =>
        (OperationPolicy)typeof(OperationPolicy).GetMethod(nameof(OperationPolicy.ForTrusted))!
            .MakeGenericMethod(type).Invoke(null,
                ["tests.component." + type.Assembly.GetName().Name!.Split('.')[2].ToLowerInvariant() + "." + type.Name.ToLowerInvariant(),
                    new[] { kind }])!;

    internal static void AddJobs(IServiceCollection services, params Type[] requests) =>
        services.AddSingleton<IOperationPolicySource>(new DeclaredPolicies(requests.Select(type => Trusted(type, AuthorizationSourceKind.BackgroundJob)).ToArray()));

    internal static void AddPublic(IServiceCollection services, Type request) =>
        services.AddSingleton<IOperationPolicySource>(new PublicPolicy(request));

    internal static void AddOrganizationAudit(IServiceCollection services, params Type[] requests) =>
        services.AddSingleton<IAuditCatalogSource>(new OrganizationAudit(requests));

    private sealed class DeclaredPolicies(OperationPolicy[] policies) : IOperationPolicySource
    {
        public IEnumerable<PermissionRegistration> Definitions => [];
        public IEnumerable<OperationPolicy> Policies => policies;
    }

    private sealed class PublicPolicy(Type request) : IOperationPolicySource
    {
        private static readonly PermissionDefinition Read = new(PermissionScope.Tenant, "jobfixture.submission.read");
        public IEnumerable<PermissionRegistration> Definitions => [new(Read, false)];
        public IEnumerable<OperationPolicy> Policies =>
        [(OperationPolicy)typeof(OperationPolicy).GetMethod(nameof(OperationPolicy.ForPublic))!
            .MakeGenericMethod(request).Invoke(null, ["tests.component.jobfixture.public_submission", Read])!];
    }

    private sealed class OrganizationAudit(Type[] requests) : IAuditCatalogSource
    {
        public string ModuleName => "jobfixture";
        public void Describe(IAuditCatalogBuilder builder)
        {
            foreach (var request in requests)
                typeof(IAuditCatalogBuilder).GetMethod(nameof(IAuditCatalogBuilder.MustAudit))!
                    .MakeGenericMethod(request).Invoke(builder, [OrganizationOperation, OperationType.Create, typeof(Organization)]);
        }
    }

    // These test-module commands exercise the real aggregate/store/audit owner.
    // Production CreateOrganizationCommand remains Seed-only and is tested as refused.
    internal static string OrganizationSource(bool component = false)
    {
        var name = component ? "ComponentCreateFixtureOrganization" : "CreateFixtureOrganization";
        return $$"""
            public sealed record {{name}}(LearnStack.SharedKernel.Identifiers.OrganizationId OrganizationId, string Slug, string DisplayName)
                : MediatR.IRequest<LearnStack.SharedKernel.Results.Result<LearnStack.Modules.Tenancy.Application.Contracts.Tenant.OrganizationDto>>;
            public sealed class {{name}}Handler(
                LearnStack.Modules.Tenancy.Application.Abstractions.IOrganizationWriteStore store,
                LearnStack.SharedKernel.Tenancy.ITenantContext context,
                LearnStack.SharedKernel.Time.IClock clock)
                : MediatR.IRequestHandler<{{name}}, LearnStack.SharedKernel.Results.Result<LearnStack.Modules.Tenancy.Application.Contracts.Tenant.OrganizationDto>>
            {
                public async System.Threading.Tasks.Task<LearnStack.SharedKernel.Results.Result<LearnStack.Modules.Tenancy.Application.Contracts.Tenant.OrganizationDto>> Handle(
                    {{name}} request, System.Threading.CancellationToken token)
                {
                    var organization = LearnStack.Modules.Tenancy.Domain.Organization.Create(request.OrganizationId,
                        context.TenantId, request.Slug, request.DisplayName, clock,
                        context.UserId ?? LearnStack.SharedKernel.Identifiers.UserId.SystemActor);
                    await store.AddAsync(organization, token);
                    return LearnStack.SharedKernel.Results.Result.Ok(new LearnStack.Modules.Tenancy.Application.Contracts.Tenant.OrganizationDto(
                        organization.Id, organization.TenantId, organization.Slug));
                }
            }
            """;
    }
}
