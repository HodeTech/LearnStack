namespace LearnStack.Application.Persistence;

/// <summary>Closed payload exclusion census. This carries no authorization.</summary>
internal static class CustomFieldDiagnosticOperations
{
    private static readonly Dictionary<string, string[]> Requests = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["LearnStack.Modules.Customization.Application.Contracts"] =
        [
            "CustomFields.RegisterCustomFieldDefinitionCommand", "CustomFields.ReviseCustomFieldDefinitionDraftCommand",
            "CustomFields.PublishCustomFieldDefinitionCommand", "CustomFields.RetireCustomFieldDefinitionCommand",
            "CustomFields.DiscardCustomFieldDefinitionDraftCommand", "CustomFields.ListCustomFieldDefinitionsQuery",
            "CustomFields.ReadCustomFieldDefinitionReceiptQuery", "CustomFields.GetCustomFieldDefinitionQuery", "CustomFields.ListCustomFieldDefinitionRevisionsQuery",
        ],
        ["LearnStack.Modules.Identity.Application.Contracts"] =
        [
            "CustomFields.GetMembershipCustomFieldsQuery", "CustomFields.GetOwnMembershipCustomFieldsQuery",
            "CustomFields.SetMembershipCustomFieldsCommand", "CustomFields.SetOwnMembershipCustomFieldsCommand",
        ],
        ["LearnStack.Modules.Tenancy.Application.Contracts"] =
        [
            "FieldConsents.GrantOwnMembershipFieldConsentCommand", "FieldConsents.WithdrawOwnMembershipFieldConsentCommand",
            "FieldConsents.GetOwnMembershipFieldConsentQuery", "FieldConsents.ListOwnFieldConsentScopesQuery",
            "FieldConsents.ListOwnMembershipFieldConsentsQuery",
            "FieldConsents.ReadOwnGrantedFieldConsentReceiptQuery", "FieldConsents.ReadOwnWithdrawnFieldConsentReceiptQuery",
        ],
    };

    internal static bool Contains(Type? type) => type is not null
        && type.Assembly.GetName().Name is { } assembly && Requests.TryGetValue(assembly, out var requests)
        && requests.Any(request => string.Equals(type.FullName, assembly + "." + request, StringComparison.Ordinal));
}
