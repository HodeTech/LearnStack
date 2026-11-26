using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Identity.Application.DataRequests;

/// <summary>
/// What the data-request executors and admissions share: which requests a caller reaches and mutates, how an identifier
/// is minted and judged, and how a refusal is spelled.
/// </summary>
internal static class DataRequestRules
{
    /// <summary>The request when the caller's role reaches it in the announced group; <see langword="null"/> otherwise.</summary>
    internal static async Task<DataSubjectRequest?> FindVisibleAsync(IDataRequestReader reader, DataRequestCaller caller, Guid requestId,
        CancellationToken cancellationToken)
    {
        if (!ValidId(requestId))
        {
            return null;
        }

        var request = await reader.FindAsync(DataSubjectRequestId.From(requestId), cancellationToken).ConfigureAwait(false);
        return request is not null && Reaches(caller, request) ? request : null;
    }

    /// <summary>A subject reaches requests about them; an administrator every export and erasure of the group, never a closure.</summary>
    private static bool Reaches(DataRequestCaller caller, DataSubjectRequest request) => caller.Role == DataRequestRole.Subject
        ? request.SubjectUserId == caller.Actor
        : request.Kind != DataRequestKind.Closure;

    /// <summary>A subject mutates requests about them; an administrator only an export or erasure they filed.</summary>
    internal static bool Mutates(DataRequestCaller caller, DataSubjectRequest request) => caller.Role == DataRequestRole.Subject
        ? request.SubjectUserId == caller.Actor
        : request.Kind != DataRequestKind.Closure && request.Origin == DataRequestOrigin.Administrator
            && request.RequesterUserId == caller.Actor;

    internal static Guid NewId(IGuidFactory factory)
    {
        var id = factory.NewUuidV7();
        return id != Guid.Empty && id.Version == 7 ? id : throw new InvalidOperationException("The identity factory must return a UUIDv7.");
    }

    internal static bool ValidId(Guid id) => id != Guid.Empty && id.Version == 7;

    internal static Result<T> AlreadyActive<T>() => Rule<T>("$", "lockey_request_already_active");

    /// <summary>A 409 with the reason on the field it concerns; the status map is closed, so the code stays the business rule's.</summary>
    internal static Result<T> Rule<T>(string field, string reason) => Result<T>.Fail(new Error(
        LocalizedMessage.Of("lockey_business_rule_violation"),
        new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal) { [field] = [LocalizedMessage.Of(reason)] }));

    internal static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));
}
