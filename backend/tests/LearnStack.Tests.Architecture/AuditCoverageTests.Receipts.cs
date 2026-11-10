using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Audit;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ReceiptCensus = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuditCoverageTests
{
    private static void AssertReceiptBindings()
    {
        (Type Request, string Module, string Operation, Type Root)[] expected =
        [
            (typeof(ReadMembershipReceiptQuery), "identity", "identity.membership.read_receipt", typeof(Membership)),
            (typeof(ReadMemberProfileReceiptQuery), "identity", "identity.membership_profile.read_receipt", typeof(Membership)),
            (typeof(ReadOwnMembershipProfileReceiptQuery), "identity", "identity.membership_profile.read_receipt", typeof(Membership)),
            (typeof(ReadRoleReceiptQuery), "identity", "identity.role.read_receipt", typeof(Role)),
            (typeof(ReadOrganizationReceiptQuery), "tenancy", "tenancy.organization.read_receipt", typeof(LearnStack.Modules.Tenancy.Domain.Organization)),
            (typeof(ReadDefaultOrganizationReceiptQuery), "tenancy", "tenancy.organization.read_receipt", typeof(LearnStack.Modules.Tenancy.Domain.Tenant)),
            (typeof(ReadInvitationReceiptQuery), "identity", "identity.invitation.read_receipt", typeof(Invitation)),
            (typeof(ReadInvitationPolicyReceiptQuery), "tenancy", "tenancy.setting.read_receipt", typeof(LearnStack.Modules.Tenancy.Domain.TenantSetting)),
            (typeof(ReadCustomFieldDefinitionReceiptQuery), "customization", "customization.custom_field.read_receipt", typeof(LearnStack.Modules.Customization.Domain.TenantCustomFieldDef)),
            (typeof(ReadOwnGrantedFieldConsentReceiptQuery), "tenancy", "tenancy.membership_field_consent.read_grant_receipt", typeof(LearnStack.Modules.Tenancy.Domain.MembershipFieldConsent)),
            (typeof(ReadOwnWithdrawnFieldConsentReceiptQuery), "tenancy", "tenancy.membership_field_consent.read_withdraw_receipt", typeof(LearnStack.Modules.Tenancy.Domain.MembershipFieldConsent)),
        ];
        var catalog = Catalogue();
        foreach (var declaration in expected)
        {
            catalog.TryGet(declaration.Request, out var registration).Should().BeTrue();
            var entry = new AuditCatalogEntry(declaration.Module, declaration.Operation, OperationType.ReadSensitive, OperationClass.Must, declaration.Root);
            BindingMatches(registration, entry).Should().BeTrue("each receipt has exactly its original root and a durable read event");
            BindingMatches(registration with { WritesNoRow = true }, entry).Should().BeFalse();
            BindingMatches(registration with { Entries = [] }, entry).Should().BeFalse();
            BindingMatches(registration with { Entries = [entry, entry] }, entry).Should().BeFalse();
            foreach (var dirty in new[]
            {
                entry with { ModuleName = declaration.Module == "customization" ? "identity" : "customization" }, entry with { Operation = "identity.receipt.control" },
                entry with { OperationType = OperationType.Update }, entry with { OperationClass = OperationClass.Should },
                entry with { EntityType = declaration.Root == typeof(Membership) ? typeof(Role) : typeof(Membership) },
            }) BindingMatches(registration with { Entries = [dirty] }, entry).Should().BeFalse();
        }
        catalog.TryGet(typeof(GetOwnMembershipProfileQuery), out var ordinaryOwn).Should().BeTrue();
        ordinaryOwn.WritesNoRow.Should().BeTrue("ordinary own-profile navigation remains distinct from the MUST receipt act");
        ordinaryOwn.Entries.Should().BeEmpty();
        catalog.TryGet(typeof(GetTenantInvitationPolicyQuery), out var ownPolicy).Should().BeTrue();
        ownPolicy.WritesNoRow.Should().BeTrue("the ordinary policy getter is Off; its original-target receipt is independently MUST");
        ownPolicy.Entries.Should().BeEmpty();
        AssertReceiptTargetDesignation();
    }

    private static readonly (string Path, string Type, string Method, string Read, string Id, string? Request, string Variable)[] ReceiptSubjects =
    [
        ("Modules/Identity/LearnStack.Modules.Identity.Application/Memberships/MembershipReceiptQueryHandlers.cs",
            "LearnStack.Modules.Identity.Application.Memberships.MembershipReceiptQueryHandlerBase", "ExecuteAsync",
            "reader.ReadMembershipAsync(MembershipId.From(id), cancellationToken)", "LearnStack.Modules.Identity.Domain.MembershipId", null, "target"),
        ("Modules/Identity/LearnStack.Modules.Identity.Application/Roles/ReadRoleReceiptQueryHandler.cs",
            "LearnStack.Modules.Identity.Application.Roles.ReadRoleReceiptQueryHandler", "Handle",
            "reader.ReadRoleAsync(RoleId.From(request.RoleId), cancellationToken)", "LearnStack.Modules.Identity.Domain.RoleId", null, "target"),
        ("Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Administration/OrganizationReceiptQueryHandlers.cs",
            "LearnStack.Modules.Tenancy.Application.Administration.ReadOrganizationReceiptQueryHandler", "Handle",
            "reader.ReadOrganizationReceiptAsync(request.OrganizationId, cancellationToken)", "LearnStack.SharedKernel.Identifiers.OrganizationId", null, "target"),
        ("Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Administration/OrganizationReceiptQueryHandlers.cs",
            "LearnStack.Modules.Tenancy.Application.Administration.ReadDefaultOrganizationReceiptQueryHandler", "Handle",
            "reader.ReadDefaultOrganizationReceiptAsync(request.TenantId, cancellationToken)", "LearnStack.SharedKernel.Identifiers.TenantId", null, "target"),
        ("Modules/Identity/LearnStack.Modules.Identity.Application/Invitations/InvitationAdministrationHandlers.cs",
            "LearnStack.Modules.Identity.Application.Invitations.InvitationAdministrationHandlers", "Handle",
            "store.ReadAsync(InvitationId.From(request.InvitationId), cancellationToken)", "LearnStack.Modules.Identity.Domain.InvitationId", "ReadInvitationReceiptQuery", "target"),
        ("Modules/Customization/LearnStack.Modules.Customization.Application/CustomFields/ReadCustomFieldDefinitionReceiptQueryHandler.cs",
            "LearnStack.Modules.Customization.Application.CustomFields.ReadCustomFieldDefinitionReceiptQueryHandler", "Handle",
            "store.FindAsync(TenantCustomFieldDefId.From(request.DefinitionId), cancellationToken)", "LearnStack.Modules.Customization.Domain.TenantCustomFieldDefId", null, "row"),
        ("Modules/Tenancy/LearnStack.Modules.Tenancy.Application/FieldConsents/FieldConsentReceiptQueryHandlers.cs",
            "LearnStack.Modules.Tenancy.Application.FieldConsents.ReadOwnGrantedFieldConsentReceiptQueryHandler", "Handle",
            "reader.ReadAsync(context.UserId!.Value, admitted, request.EvidenceId, cancellationToken)", "LearnStack.Modules.Tenancy.Domain.MembershipFieldConsentId", null, "target"),
        ("Modules/Tenancy/LearnStack.Modules.Tenancy.Application/FieldConsents/FieldConsentReceiptQueryHandlers.cs",
            "LearnStack.Modules.Tenancy.Application.FieldConsents.ReadOwnWithdrawnFieldConsentReceiptQueryHandler", "Handle",
            "reader.ReadAsync(witness.Actor, [membership], request.EvidenceId, cancellationToken)", "LearnStack.Modules.Tenancy.Domain.MembershipFieldConsentId", null, "target"),
        ("Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Settings/InvitationPolicyHandlers.cs",
            "LearnStack.Modules.Tenancy.Application.Settings.InvitationPolicyHandlers", "Handle",
            "reader.ReadReceiptAsync(TenantSettingId.From(request.SettingId), cancellationToken)", "LearnStack.Modules.Tenancy.Domain.TenantSettingId", "ReadInvitationPolicyReceiptQuery", "row"),
    ];

    private static void AssertReceiptTargetDesignation()
    {
        using var census = ReceiptCensus.Read();
        ReceiptDesignationOffenders(census).Should().BeEmpty();
        foreach (var declaration in ReceiptSubjects)
        {
            var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, declaration.Path));
            ReceiptSubjectIsRetained(source, declaration.Type.Split('.').Last(), declaration.Method, declaration.Read, declaration.Request, declaration.Variable).Should().BeTrue();
            var designator = declaration.Type.Contains("FieldConsentReceiptQueryHandler", StringComparison.Ordinal) ? "audit" : "subject";
            foreach (var (before, after) in new[]
            {
                (declaration.Read, declaration.Read.Replace("reader.", "substitute.", StringComparison.Ordinal).Replace("store.", "substitute.", StringComparison.Ordinal)),
                (designator + ".Designate(" + declaration.Variable + ")", designator + ".Designate(successor)"),
                (designator + ".Designate(" + declaration.Variable + ")", designator + ".DesignateProjection(" + declaration.Variable + ")"),
            })
            {
                var dirty = source.Replace(before, after, StringComparison.Ordinal);
                dirty.Should().NotBe(source);
                ReceiptSubjectIsRetained(dirty, declaration.Type.Split('.').Last(), declaration.Method, declaration.Read, declaration.Request, declaration.Variable).Should().BeFalse(
                    "the successful receipt designates the actual exact-ID lookup, never a current successor or a projection alias");
            }
            var body = ReceiptBody(census, declaration.Type, declaration.Method, declaration.Request);
            var site = body.Body.Instructions.Single(IsReceiptDesignation);
            var opcode = site.OpCode;
            var operand = site.Operand;
            foreach (var replacement in new[] { OpCodes.Nop, OpCodes.Ldvirtftn, OpCodes.Call })
            {
                site.OpCode = replacement;
                try { ReceiptDesignationOffenders(census).Should().NotBeEmpty("each actual typed designation is required and cannot become an executable alias"); }
                finally { site.OpCode = opcode; }
            }
            var generic = (GenericInstanceMethod)operand;
            var id = generic.GenericArguments[0];
            generic.GenericArguments[0] = body.Module.ImportReference(typeof(Guid));
            try { ReceiptDesignationOffenders(census).Should().NotBeEmpty("the original aggregate identifier type is part of audit attribution"); }
            finally { generic.GenericArguments[0] = id; }
            var duplicate = Instruction.Create(OpCodes.Callvirt, (MethodReference)operand);
            body.Body.Instructions.Add(duplicate);
            try { ReceiptDesignationOffenders(census).Should().NotBeEmpty("another designation cannot hide behind the approved reader"); }
            finally { body.Body.Instructions.Remove(duplicate); }
        }
        ReceiptDesignationOffenders(census).Should().BeEmpty("all compiled receipt subjects are restored");
    }

    private static List<string> ReceiptDesignationOffenders(ReceiptCensus census)
    {
        var errors = new List<string>();
        foreach (var declaration in ReceiptSubjects)
        {
            var sites = ReceiptBody(census, declaration.Type, declaration.Method, declaration.Request).Body.Instructions.Where(IsReceiptDesignation).ToArray();
            if (sites.Length != 1 || sites[0].OpCode != OpCodes.Callvirt || sites[0].Operand is not GenericInstanceMethod call
                || call.Name != "Designate" || call.GenericArguments.Count != 1 || call.GenericArguments[0].FullName != declaration.Id)
                errors.Add("Receipt root designation changed: " + declaration.Type);
        }
        return errors;
    }

    private static bool IsReceiptDesignation(Instruction instruction) => instruction.Operand is MethodReference target
        && target.DeclaringType.FullName == typeof(IAuditSubject).FullName
        && target.Name is "Designate" or "DesignateProjection";

    private static MethodDefinition ReceiptBody(ReceiptCensus census, string type, string method, string? request)
    {
        if (request is null) return census.Body(type, method);
        var owner = census.Type(type).Methods.Single(candidate => candidate.Name == method
            && candidate.Parameters.Count > 0 && candidate.Parameters[0].ParameterType.Name == request);
        var state = owner.CustomAttributes.SingleOrDefault(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        return state is null ? owner : census.Type(((TypeReference)state.ConstructorArguments[0].Value).GetElementType().FullName)
            .Methods.Single(candidate => candidate.Name == "MoveNext");
    }

    private static bool ReceiptSubjectIsRetained(string source, string handler, string method, string read, string? request, string variable)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var body = root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(type => type.Identifier.ValueText == handler)?
            .Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(member => member.Identifier.ValueText == method
                && (request is null || member.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString() == request));
        if (root.ContainsDiagnostics || body is null) return false;
        var declarations = body.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(declaration => declaration.Identifier.ValueText == variable).ToArray();
        var designator = handler.Contains("FieldConsentReceiptQueryHandler", StringComparison.Ordinal) ? "audit" : "subject";
        var designations = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax access && access.Expression.ToString() == designator).ToArray();
        return declarations.Length == 1 && declarations[0].Initializer?.Value is AwaitExpressionSyntax awaited
            && SyntaxFactory.AreEquivalent(awaited.Expression, SyntaxFactory.ParseExpression(read))
            && designations.Length == 1 && SyntaxFactory.AreEquivalent(designations[0], SyntaxFactory.ParseExpression(designator + ".Designate(" + variable + ")"));
    }
}
