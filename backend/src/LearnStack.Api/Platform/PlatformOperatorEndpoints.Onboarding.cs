using LearnStack.Api.Authentication;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Identity.Application.Contracts.OperatorActs;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using MediatR;
using LearnStack.Api.Common;

namespace LearnStack.Api.Platform;

/// <summary>The default organization of a tenant being provisioned.</summary>
internal sealed record DefaultOrganizationBody(Guid Id, string? Slug, string? DisplayName);

/// <summary>The body of a tenant provisioning.</summary>
internal sealed record TenantProvisionRequest(
    string? Slug, string? DisplayName, DefaultOrganizationBody? DefaultOrganization, string? Ticket);

/// <summary>The body of a host mapping.</summary>
internal sealed record HostMapRequest(Guid? OrganizationId, bool? IsActive, bool? IsPubliclyLive, string? Ticket);

/// <summary>The body of a role template initialization.</summary>
internal sealed record RoleInitializeRequest(string? BuiltInKey, string? CapabilityScope, string? Ticket);

/// <summary>The body of a first-administrator or recovery act.</summary>
internal sealed record AdministratorRequest(Guid RoleId, string? ProviderSubject, string? Ticket);

/// <summary>What an operator act answers: its result and the correlation id the operator records.</summary>
internal sealed record OperatorActResponse<T>(T Result, string CorrelationId);

/// <summary>
/// The onboarding routes of the operator surface (ADR-0102): provision a tenant, map its host,
/// initialize a role template, create the first administrator, recover an administrator.
/// </summary>
/// <remarks>
/// Five idempotent <c>PUT</c>s on operator-supplied identifiers, so a reply lost on the wire is
/// repaired by sending the same request again. Each runs the gate first, then the act through the
/// ordinary pipeline on the tenant the route names (<see cref="PlatformOperatorActExecutor"/>).
/// </remarks>
internal static partial class PlatformOperatorEndpoints
{
    private static void MapOnboarding(RouteGroupBuilder group)
    {
        Describe(
            group.MapPut("/tenants/{tenantId:guid}", ProvisionTenantAsync)
                .WithName("PlatformTenantProvision")
                .WithSummary("Provision a tenant and its default organization (idempotent on the id and slug)."),
            typeof(OperatorActResponse<ProvisionedTenantDto>));
        Describe(
            group.MapPut("/tenants/{tenantId:guid}/host-mappings/{host}", MapHostAsync)
                .WithName("PlatformHostMap")
                .WithSummary("Map a host to a tenant; the operator asserts ownership of the host."),
            typeof(OperatorActResponse<HostMappingDto>));
        Describe(
            group.MapPut("/tenants/{tenantId:guid}/roles/{roleId:guid}", InitializeRoleAsync)
                .WithName("PlatformRoleInitialize")
                .WithSummary("Initialize a built-in role template before any membership exists."),
            typeof(OperatorActResponse<Guid>));
        Describe(
            group.MapPut("/tenants/{tenantId:guid}/first-administrator/{membershipId:guid}", CreateFirstAdminAsync)
                .WithName("PlatformFirstAdminCreate")
                .WithSummary("Create the first administrator of a tenant that has never had a member."),
            typeof(OperatorActResponse<Guid>));
        Describe(
            group.MapPut("/tenants/{tenantId:guid}/administrator-recovery/{membershipId:guid}", RecoverAdminAsync)
                .WithName("PlatformAdminRecover")
                .WithSummary("Create a replacement administrator for a tenant with none active."),
            typeof(OperatorActResponse<Guid>));
    }

    private static void Describe(RouteHandlerBuilder route, Type success) => route
        .Produces(StatusCodes.Status200OK, success, "application/json")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static Task<IResult> ProvisionTenantAsync(
        Guid tenantId, HttpContext http, IPlatformOperatorGate gate, PlatformOperatorActExecutor executor,
        ILoggerFactory loggers, CancellationToken cancellationToken) =>
        RunAsync<TenantProvisionRequest, ProvisionedTenantDto>(
            http, tenantId, PlatformOperations.TenantProvision, gate, executor, loggers,
            body => body.Ticket,
            body => body.Slug is { } slug && body.DisplayName is { } name
                && body.DefaultOrganization is { Slug: { } orgSlug, DisplayName: { } orgName } organization
                && IsRoot(organization.Id)
                ? new OperatorProvisionTenantCommand(
                    TenantId.From(tenantId), slug, name, OrganizationId.From(organization.Id), orgSlug, orgName)
                : null,
            cancellationToken);

    private static Task<IResult> MapHostAsync(
        Guid tenantId, string host, HttpContext http, IPlatformOperatorGate gate, PlatformOperatorActExecutor executor,
        ILoggerFactory loggers, CancellationToken cancellationToken) =>
        RunAsync<HostMapRequest, HostMappingDto>(
            http, tenantId, PlatformOperations.HostMap, gate, executor, loggers,
            body => body.Ticket,
            // Both flags are the operator's to state: there is no act that unmaps a host, so a mapping must
            // never arrive serving anonymous traffic because a field was left out.
            body => body.IsActive is { } active && body.IsPubliclyLive is { } live
                ? new OperatorMapHostCommand(
                    host, body.OrganizationId is { } organization ? OrganizationId.From(organization) : null, active, live)
                : null,
            cancellationToken);

    private static Task<IResult> InitializeRoleAsync(
        Guid tenantId, Guid roleId, HttpContext http, IPlatformOperatorGate gate, PlatformOperatorActExecutor executor,
        ILoggerFactory loggers, CancellationToken cancellationToken) =>
        RunAsync<RoleInitializeRequest, Guid>(
            http, tenantId, PlatformOperations.RoleInitialize, gate, executor, loggers,
            body => body.Ticket,
            body => Enum.TryParse<BuiltInRoleKey>(body.BuiltInKey, ignoreCase: true, out var key) && Enum.IsDefined(key)
                && Enum.TryParse<PermissionScope>(body.CapabilityScope, ignoreCase: true, out var scope)
                && scope is PermissionScope.Tenant or PermissionScope.Organization
                && IsRoot(roleId)
                ? new OperatorInitializeRoleCommand(roleId, key, scope)
                : null,
            cancellationToken);

    private static Task<IResult> CreateFirstAdminAsync(
        Guid tenantId, Guid membershipId, HttpContext http, IPlatformOperatorGate gate, PlatformOperatorActExecutor executor,
        ILoggerFactory loggers, CancellationToken cancellationToken) =>
        RunAsync<AdministratorRequest, Guid>(
            http, tenantId, PlatformOperations.FirstAdminCreate, gate, executor, loggers,
            body => body.Ticket,
            body => IsSubject(body.ProviderSubject) && IsRoot(membershipId) && IsRoot(body.RoleId)
                ? new OperatorCreateFirstAdminCommand(membershipId, body.RoleId, body.ProviderSubject!) : null,
            cancellationToken);

    private static Task<IResult> RecoverAdminAsync(
        Guid tenantId, Guid membershipId, HttpContext http, IPlatformOperatorGate gate, PlatformOperatorActExecutor executor,
        ILoggerFactory loggers, CancellationToken cancellationToken) =>
        RunAsync<AdministratorRequest, Guid>(
            http, tenantId, PlatformOperations.AdminRecover, gate, executor, loggers,
            body => body.Ticket,
            body => IsSubject(body.ProviderSubject) && IsRoot(membershipId) && IsRoot(body.RoleId)
                ? new OperatorRecoverAdminCommand(membershipId, body.RoleId, body.ProviderSubject!) : null,
            cancellationToken);

    /// <summary>
    /// The shared flow of an onboarding act: the credential, the body read after admission, the gate,
    /// the act, and the answer.
    /// </summary>
    private static async Task<IResult> RunAsync<TBody, TResult>(
        HttpContext http,
        Guid tenantRoute,
        PlatformOperation operation,
        IPlatformOperatorGate gate,
        PlatformOperatorActExecutor executor,
        ILoggerFactory loggers,
        Func<TBody, string?> ticketOf,
        Func<TBody, IRequest<Result<TResult>>?> build,
        CancellationToken cancellationToken)
        where TBody : class
    {
        var witness = http.Features.Get<PlatformOperatorFeature>()?.Witness;
        if (witness is null)
        {
            return Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized");
        }

        // Admission first, as the killswitch route does: the body is read before the gate is asked
        // (the gate needs its ticket) but never answered until the gate has admitted the act.
        var (body, bodyState) = await ReadBodyAsync<TBody>(http.Request, cancellationToken);
        var admission = await gate.AdmitAsync(witness, operation, body is null ? null : ticketOf(body), cancellationToken);
        if (!admission.IsAdmitted)
        {
            return RefusalProblem(http, admission.Refusal);
        }

        if (bodyState == BodyState.NotJson)
        {
            return Problem(http, StatusCodes.Status415UnsupportedMediaType, "lockey_unsupported_media_type");
        }

        if (body is null || !IsRoot(tenantRoute) || build(body) is not { } request)
        {
            return Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed");
        }

        try
        {
            var result = await executor.ExecuteAsync(admission.Admission!, TenantId.From(tenantRoute), request, cancellationToken);
            return result.IsSuccess
                ? Results.Ok(new OperatorActResponse<TResult>(
                    result.Value!, admission.Admission!.Operator.CorrelationId ?? http.TraceIdentifier))
                : ResultProblem(http, result.Error!);
        }
        catch (AuditWriteFailedException failure)
        {
            PlatformOperatorMetrics.AuditFailed(operation);
            LogAuditRefused(loggers.CreateLogger(typeof(PlatformOperatorEndpoints).FullName!), failure);
            RetryAfter.Apply(http.Response, StatusCodes.Status503ServiceUnavailable);
            return Problem(http, StatusCodes.Status503ServiceUnavailable, "lockey_audit_unavailable");
        }
    }

    /// <summary>
    /// An identifier the operator generates: a UUIDv7, never the nil value and never the platform sentinel, which
    /// is the tenant the platform's own rows belong to and which no operator act may name.
    /// </summary>
    private static bool IsRoot(Guid id) => id != Guid.Empty && id.Version == 7 && id != TenantId.PlatformSentinel.Value;

    private const int MaximumSubjectLength = 255;

    private static bool IsSubject(string? subject) =>
        !string.IsNullOrWhiteSpace(subject) && subject.Length <= MaximumSubjectLength;

    private static IResult RefusalProblem(HttpContext http, PlatformRefusal refusal) => refusal switch
    {
        PlatformRefusal.InvalidTicket => Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed"),
        PlatformRefusal.InvalidWitness => Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized"),
        _ => Problem(http, StatusCodes.Status403Forbidden, "lockey_forbidden"),
    };

    /// <summary>An act's own refusal, in the status and shape the API gives every other failed result.</summary>
    private static IResult ResultProblem(HttpContext http, Error error) =>
        Results.Json(
            ProblemDetailsFactory.For(error, http),
            options: null,
            contentType: ProblemDetailsMediaType.Value,
            statusCode: HttpStatusMap.For(error));
}
