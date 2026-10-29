using System.Globalization;
using System.Text;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Idempotency;

/// <summary>Only actual typed dispatch can construct a receipt and its original immutable target.</summary>
internal sealed class ProtectedAdministrationResult : IActionResult
{
    private ProtectedAdministrationResult(ProtectedAdministrationDescriptor descriptor, IdempotencyReplayTarget target,
        long version, long? expectedVersion, ITenantContext context)
    {
        Descriptor = descriptor; Target = target; Version = version; ExpectedVersion = expectedVersion;
        CapturedTenant = context.TenantId; CapturedOrganization = context.OrganizationId; Actor = context.UserId;
    }

    internal ProtectedAdministrationDescriptor Descriptor { get; }
    internal IdempotencyReplayTarget Target { get; }
    internal long Version { get; }
    internal long? ExpectedVersion { get; }
    private TenantId CapturedTenant { get; }
    private OrganizationId? CapturedOrganization { get; }
    private UserId? Actor { get; }

    internal static async Task<IActionResult> ExecuteAsync(HttpContext http, ISender sender, ITenantContext context,
        object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var operation = ProtectedAdministrationDescriptor.OperationFor(request);
        if (operation is null || !context.IsResolved || context.UserId is null
            || context.UserId == UserId.SystemActor || !StronglyTypedId.IsAssigned(context.TenantId)
            || context.TenantId == TenantId.PlatformSentinel
            || http.GetEndpoint()?.Metadata.GetMetadata<IdempotentAttribute>()?.ProtectedOperation != operation)
            return Failure("lockey_forbidden");
        var descriptor = ProtectedAdministrationDescriptor.For(operation.Value);
        var expected = ProtectedAdministrationDescriptor.ExpectedVersion(request);
        if (descriptor.Creates ? http.Request.Headers.IfMatch.Count != 0
            : !ProtectedEntityTag.TryRead(http.Request, out var asserted) || asserted != expected)
            return new ProblemDetailsActionResult(ProtectedEntityTag.Invalid());

        // Explicit typed sends bind each result to its exact verified command.
        // No request JSON, HttpContext.Items or caller-supplied target can create this carrier.
        return request switch
        {
            SetMembershipProfileCommand command => Membership(await sender.Send(command, cancellationToken), command.MembershipId),
            SetOwnMembershipProfileCommand command => Membership(await sender.Send(command, cancellationToken), null),
            ChangeMembershipStatusCommand command => Membership(await sender.Send(command, cancellationToken), command.MembershipId),
            ReplaceMembershipRoleAssignmentsCommand command => Membership(await sender.Send(command, cancellationToken), command.MembershipId),
            ChangeRoleGrantsCommand command => Versioned(await sender.Send(command, cancellationToken), command.RoleId),
            AdoptRoleGrantManifestCommand command => Versioned(await sender.Send(command, cancellationToken), command.RoleId),
            CreateManagedOrganizationCommand command => Organization(await sender.Send(command, cancellationToken), null),
            RenameOrganizationCommand command => Organization(await sender.Send(command, cancellationToken), command.OrganizationId),
            ArchiveOrganizationCommand command => Organization(await sender.Send(command, cancellationToken), command.OrganizationId),
            SelectDefaultOrganizationCommand command => Versioned(await sender.Send(command, cancellationToken), context.TenantId.Value),
            _ => throw new InvalidOperationException("The protected operation is not registered."),
        };

        IActionResult Membership(Result<MembershipMutationResult> result, Guid? requested) => result.IsFailure
            ? new ProblemDetailsActionResult(result.Error!)
            : Create(result.Value!.MembershipId, result.Value.Version, requested);
        IActionResult Organization(Result<OrganizationMutationResult> result, OrganizationId? requested) => result.IsFailure
            ? new ProblemDetailsActionResult(result.Error!)
            : Create(StronglyTypedId.IsAssigned(result.Value!.OrganizationId) ? result.Value.OrganizationId.Value : Guid.Empty,
                result.Value.Version, requested is { } id ? StronglyTypedId.IsAssigned(id) ? id.Value : Guid.Empty : null);
        IActionResult Versioned(Result<long> result, Guid target) => result.IsFailure
            ? new ProblemDetailsActionResult(result.Error!) : Create(target, result.Value, target);
        IActionResult Create(Guid target, long version, Guid? requested)
        {
            // A malformed successful result remains a success-shaped invalid carrier:
            // the filter must tombstone it, never release a possibly committed mutation.
            var validTarget = target != Guid.Empty && target.Version == 7 && (requested is null || target == requested);
            return new ProtectedAdministrationResult(descriptor,
                new IdempotencyReplayTarget(descriptor.TargetKind, validTarget ? target : Guid.Empty), version, expected, context);
        }
    }

    internal bool Matches(ProtectedAdministrationDescriptor descriptor, ITenantContext context, long? expected) =>
        Descriptor == descriptor && ExpectedVersion == expected && Version >= 0
        && Target.Kind == descriptor.TargetKind && Target.Id != Guid.Empty && Target.Id.Version == 7
        && context.IsResolved && CapturedTenant == context.TenantId && CapturedOrganization == context.OrganizationId && Actor == context.UserId
        && (Target.Kind != IdempotencyReplayTargetKind.Tenant || Target.Id == context.TenantId.Value);

    internal static byte[] BodyFor(long version) => Encoding.UTF8.GetBytes(
        "{\"version\":\"" + version.ToString(CultureInfo.InvariantCulture) + "\"}");
    internal static string LocationFor(Guid target) => "/api/v1/tenancy/organizations/" + target.ToString("D");

    public async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var response = context.HttpContext.Response;
        response.StatusCode = Descriptor.Creates ? StatusCodes.Status201Created : StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.ETag = EntityTag.For(Version);
        if (Descriptor.Creates) response.Headers.Location = LocationFor(Target.Id);
        // This is the filter's memory capture, not network delivery. A disconnect
        // after business commit must not prevent recording the completed receipt.
        await response.Body.WriteAsync(BodyFor(Version), CancellationToken.None);
    }

    private static ProblemDetailsActionResult Failure(string code) => new(new Error(LocalizedMessage.Of(code)));
}
