using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.FieldConsents;

public sealed class ListOwnMembershipFieldConsentsQueryHandler(OwnConsentEntryAuthorization authorization,
    IOwnMembershipFieldConsentHistory history, IExactCustomFieldDefinitionReader definitions)
    : IRequestHandler<ListOwnMembershipFieldConsentsQuery, Result<MembershipFieldConsentPage>>
{
    public async Task<Result<MembershipFieldConsentPage>> Handle(ListOwnMembershipFieldConsentsQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MembershipId == Guid.Empty || request.MembershipId.Version != 7 || request.Limit is < 1 or > 20 || request.Cursor is { Length: > 256 })
            return Fail("lockey_validation_failed");
        var witness = authorization.For(request, ConsentSubjectPurpose.History);
        if (witness is null || witness.MembershipId != request.MembershipId) return Fail("lockey_forbidden");
        var page = await history.ListLatestAsync(witness.Actor, request.MembershipId, request.Cursor, request.Limit, cancellationToken);
        if (page is null) return Fail("lockey_validation_failed");
        var pins = page.Items.Select(item => new CustomFieldDefinitionPin(item.DefinitionId, item.DefinitionVersion)).Distinct().ToArray();
        var definitionsById = (await definitions.ReadBatchAsync(pins, CustomFieldResolution.ExistingPin, null, cancellationToken))
            .ToDictionary(item => item.DefinitionId);
        var resolved = new List<MembershipFieldConsentDetails>(page.Items.Count);
        foreach (var item in page.Items)
        {
            if (!definitionsById.TryGetValue(item.DefinitionId, out var definition)
                || definition.SchemaVersion != item.DefinitionVersion || definition.PurposeKey != item.PurposeKey
                || definition.Notices is null || !definition.Notices.TryGetValue(item.NoticeLocale, out var text)
                || MembershipFieldConsentNotice.ComputeHash(text) != item.NoticeHash)
                return Fail("lockey_dependency_unavailable");
            resolved.Add(item with { NoticeText = text });
        }
        if (!ReferenceEquals(authorization.For(request, ConsentSubjectPurpose.History), witness)) return Fail("lockey_forbidden");
        return Result.Ok(new MembershipFieldConsentPage(resolved, page.NextCursor));
    }

    private static Result<MembershipFieldConsentPage> Fail(string code) => OwnFieldConsentAuthorization.Failure<MembershipFieldConsentPage>(code);
}
