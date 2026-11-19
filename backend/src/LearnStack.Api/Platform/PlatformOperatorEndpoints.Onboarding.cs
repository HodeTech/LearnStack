using System.ComponentModel.DataAnnotations;
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
/// <param name="Id">The organization's identifier, a UUIDv7 the operator generates.</param>
/// <param name="Slug">The organization's slug.</param>
/// <param name="DisplayName">The organization's display name.</param>
internal sealed record DefaultOrganizationBody(
    [property: Required] Guid Id,
    [property: Required] string? Slug,
    [property: Required] string? DisplayName);

/// <summary>The body of a tenant provisioning.</summary>
internal sealed record TenantProvisionRequest(
    [property: Required] string? Slug,
    [property: Required] string? DisplayName,
    [property: Required] DefaultOrganizationBody? DefaultOrganization,
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(TicketPattern.Value)]
    string? Ticket);

/// <summary>The body of a host mapping; both flags are required because no act unmaps a host.</summary>
internal sealed record HostMapRequest(
    Guid? OrganizationId,
    [property: Required] bool? IsActive,
    [property: Required] bool? IsPubliclyLive,
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(TicketPattern.Value)]
    string? Ticket);

/// <summary>The body of a role template initialization.</summary>
/// <param name="BuiltInKey">One of <c>TenantAdmin</c>, <c>Instructor</c>, <c>Learner</c>, <c>Guardian</c>, <c>Editor</c>, <c>OrgAdmin</c>, <c>OrgInstructor</c>.</param>
/// <param name="CapabilityScope"><c>Tenant</c> or <c>Organization</c>.</param>
/// <param name="Ticket">An optional reference of the operator's own; it is validated and not recorded.</param>
internal sealed record RoleInitializeRequest(
    [property: Required] string? BuiltInKey,
    [property: Required] string? CapabilityScope,
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(TicketPattern.Value)]
    string? Ticket);

/// <summary>The body of a first-administrator or recovery act.</summary>
/// <param name="RoleId">The initialized tenant-wide administrator Role, a UUIDv7.</param>
/// <param name="ProviderSubject">The person's <c>sub</c> at the identity authority's issuer, at most 255 characters.</param>
/// <param name="Ticket">An optional reference of the operator's own; it is validated and not recorded.</param>
internal sealed record AdministratorRequest(
    [property: Required] Guid RoleId,
    [property: Required][property: StringLength(OperatorSubjectLimits.MaximumProviderSubjectLength, MinimumLength = 1)] string? ProviderSubject,
    [property: StringLength(64, MinimumLength = 1)]
    [property: RegularExpression(TicketPattern.Value)]
    string? Ticket);

/// <summary>The ticket grammar of ADR-0101 § 5, stated once for the request records.</summary>
internal static class TicketPattern
{
    internal const string Value = @"^[A-Za-z0-9._:/#-]{1,64}$";
}

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
                .WithSummary("Provision a tenant and its default organization; an exact repeat answers from the stored record, a repeat that changes a value is refused."),
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
                && IsOperatorId(organization.Id)
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
            body => TryName<BuiltInRoleKey>(body.BuiltInKey, out var key)
                && TryName<PermissionScope>(body.CapabilityScope, out var scope)
                && scope is PermissionScope.Tenant or PermissionScope.Organization
                && IsOperatorId(roleId)
                ? new OperatorInitializeRoleCommand(roleId, key, scope)
                : null,
            cancellationToken);

    private static Task<IResult> CreateFirstAdminAsync(
        Guid tenantId, Guid membershipId, HttpContext http, IPlatformOperatorGate gate, PlatformOperatorActExecutor executor,
        ILoggerFactory loggers, CancellationToken cancellationToken) =>
        RunAsync<AdministratorRequest, Guid>(
            http, tenantId, PlatformOperations.FirstAdminCreate, gate, executor, loggers,
            body => body.Ticket,
            body => IsSubject(body.ProviderSubject) && IsOperatorId(membershipId) && IsOperatorId(body.RoleId)
                ? new OperatorCreateFirstAdminCommand(membershipId, body.RoleId, body.ProviderSubject!) : null,
            cancellationToken);

    private static Task<IResult> RecoverAdminAsync(
        Guid tenantId, Guid membershipId, HttpContext http, IPlatformOperatorGate gate, PlatformOperatorActExecutor executor,
        ILoggerFactory loggers, CancellationToken cancellationToken) =>
        RunAsync<AdministratorRequest, Guid>(
            http, tenantId, PlatformOperations.AdminRecover, gate, executor, loggers,
            body => body.Ticket,
            body => IsSubject(body.ProviderSubject) && IsOperatorId(membershipId) && IsOperatorId(body.RoleId)
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

        if (body is null || !IsOperatorId(tenantRoute) || build(body) is not { } request)
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
            return AuditUnavailableProblem(http, loggers, operation, failure);
        }
    }

    /// <summary>
    /// An identifier the operator generates: a UUIDv7, never the nil value and never the platform sentinel, which
    /// is the tenant the platform's own rows belong to and which no operator act may name.
    /// </summary>
    /// <summary>
    /// A MUST row that cannot be written refuses the act: a counter an operator can alert on, an error line, and a 503 with
    /// its delay. Nothing changed, so the act is safe to repeat.
    /// </summary>
    private static IResult AuditUnavailableProblem(
        HttpContext http, ILoggerFactory loggers, PlatformOperation operation, AuditWriteFailedException failure)
    {
        PlatformOperatorMetrics.AuditFailed(operation);
        LogAuditRefused(loggers.CreateLogger(typeof(PlatformOperatorEndpoints).FullName!), failure);
        RetryAfter.Apply(http.Response, StatusCodes.Status503ServiceUnavailable);
        return Problem(http, StatusCodes.Status503ServiceUnavailable, "lockey_audit_unavailable");
    }

    private static bool IsOperatorId(Guid id) => id != Guid.Empty && id.Version == 7 && id != TenantId.PlatformSentinel.Value;

    /// <summary>
    /// A member of the closed set by its exact name, case aside. <c>Enum.TryParse</c> is not that: it also takes
    /// numerals, padded names and comma lists that OR into another member.
    /// </summary>
    private static bool TryName<TEnum>(string? value, out TEnum member) where TEnum : struct, Enum
    {
        member = default;
        var name = Enum.GetNames<TEnum>().FirstOrDefault(
            candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out member);
    }

    private static bool IsSubject(string? subject) =>
        !string.IsNullOrWhiteSpace(subject) && subject.Length <= OperatorSubjectLimits.MaximumProviderSubjectLength;

    private static IResult RefusalProblem(HttpContext http, PlatformRefusal refusal) => refusal switch
    {
        PlatformRefusal.InvalidTicket => Problem(http, StatusCodes.Status400BadRequest, "lockey_validation_failed"),
        PlatformRefusal.InvalidWitness => Problem(http, StatusCodes.Status401Unauthorized, "lockey_unauthorized"),
        _ => Problem(http, StatusCodes.Status403Forbidden, "lockey_forbidden"),
    };

    /// <summary>An act's own refusal, in the status and shape the API gives every other failed result.</summary>
    private static IResult ResultProblem(HttpContext http, Error error)
    {
        var status = HttpStatusMap.For(error);

        // A refusal that is a lock the act could not take answers 503 as a result, and a 503 owes its delay like every
        // other the API sends.
        RetryAfter.Apply(http.Response, status);
        return Results.Json(
            ProblemDetailsFactory.For(error, http),
            options: null,
            contentType: ProblemDetailsMediaType.Value,
            statusCode: status);
    }
}
