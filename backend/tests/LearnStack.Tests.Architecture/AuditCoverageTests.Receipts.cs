using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
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
                entry with { ModuleName = "customization" }, entry with { Operation = "identity.receipt.control" },
                entry with { OperationType = OperationType.Update }, entry with { OperationClass = OperationClass.Should },
                entry with { EntityType = declaration.Root == typeof(Membership) ? typeof(Role) : typeof(Membership) },
            }) BindingMatches(registration with { Entries = [dirty] }, entry).Should().BeFalse();
        }
        catalog.TryGet(typeof(GetOwnMembershipProfileQuery), out var ordinaryOwn).Should().BeTrue();
        ordinaryOwn.WritesNoRow.Should().BeTrue("ordinary own-profile navigation remains distinct from the MUST receipt act");
        ordinaryOwn.Entries.Should().BeEmpty();
        AssertReceiptTargetDesignation();
    }

    private static readonly (string Path, string Type, string Method, string Read, string Id)[] ReceiptSubjects =
    [
        ("Modules/Identity/LearnStack.Modules.Identity.Application/Memberships/MembershipReceiptQueryHandlers.cs",
            "LearnStack.Modules.Identity.Application.Memberships.MembershipReceiptQueryHandlerBase", "ExecuteAsync",
            "reader.ReadMembershipAsync(MembershipId.From(id), cancellationToken)", "LearnStack.Modules.Identity.Domain.MembershipId"),
        ("Modules/Identity/LearnStack.Modules.Identity.Application/Roles/ReadRoleReceiptQueryHandler.cs",
            "LearnStack.Modules.Identity.Application.Roles.ReadRoleReceiptQueryHandler", "Handle",
            "reader.ReadRoleAsync(RoleId.From(request.RoleId), cancellationToken)", "LearnStack.Modules.Identity.Domain.RoleId"),
        ("Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Administration/OrganizationReceiptQueryHandlers.cs",
            "LearnStack.Modules.Tenancy.Application.Administration.ReadOrganizationReceiptQueryHandler", "Handle",
            "reader.ReadOrganizationReceiptAsync(request.OrganizationId, cancellationToken)", "LearnStack.SharedKernel.Identifiers.OrganizationId"),
        ("Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Administration/OrganizationReceiptQueryHandlers.cs",
            "LearnStack.Modules.Tenancy.Application.Administration.ReadDefaultOrganizationReceiptQueryHandler", "Handle",
            "reader.ReadDefaultOrganizationReceiptAsync(request.TenantId, cancellationToken)", "LearnStack.SharedKernel.Identifiers.TenantId"),
    ];

    private static void AssertReceiptTargetDesignation()
    {
        using var census = ReceiptCensus.Read();
        ReceiptDesignationOffenders(census).Should().BeEmpty();
        foreach (var declaration in ReceiptSubjects)
        {
            var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, declaration.Path));
            ReceiptSubjectIsRetained(source, declaration.Type.Split('.').Last(), declaration.Method, declaration.Read).Should().BeTrue();
            foreach (var (before, after) in new[]
            {
                (declaration.Read, declaration.Read.Replace("reader.", "substitute.", StringComparison.Ordinal)),
                ("subject.Designate(target)", "subject.Designate(successor)"),
                ("subject.Designate(target)", "subject.DesignateProjection(target)"),
            })
            {
                var dirty = source.Replace(before, after, StringComparison.Ordinal);
                dirty.Should().NotBe(source);
                ReceiptSubjectIsRetained(dirty, declaration.Type.Split('.').Last(), declaration.Method, declaration.Read).Should().BeFalse(
                    "the successful receipt designates the actual exact-ID lookup, never a current successor or a projection alias");
            }
            var body = census.Body(declaration.Type, declaration.Method);
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
            var sites = census.Body(declaration.Type, declaration.Method).Body.Instructions.Where(IsReceiptDesignation).ToArray();
            if (sites.Length != 1 || sites[0].OpCode != OpCodes.Callvirt || sites[0].Operand is not GenericInstanceMethod call
                || call.Name != "Designate" || call.GenericArguments.Count != 1 || call.GenericArguments[0].FullName != declaration.Id)
                errors.Add("Receipt root designation changed: " + declaration.Type);
        }
        return errors;
    }

    private static bool IsReceiptDesignation(Instruction instruction) => instruction.Operand is MethodReference target
        && target.DeclaringType.FullName == typeof(IAuditSubject).FullName
        && target.Name is "Designate" or "DesignateProjection";

    private static bool ReceiptSubjectIsRetained(string source, string handler, string method, string read)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var body = root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(type => type.Identifier.ValueText == handler)?
            .Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(member => member.Identifier.ValueText == method);
        if (root.ContainsDiagnostics || body is null) return false;
        var declarations = body.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(variable => variable.Identifier.ValueText == "target").ToArray();
        var designations = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax access && access.Expression.ToString() == "subject").ToArray();
        return declarations.Length == 1 && declarations[0].Initializer?.Value is AwaitExpressionSyntax awaited
            && SyntaxFactory.AreEquivalent(awaited.Expression, SyntaxFactory.ParseExpression(read))
            && designations.Length == 1 && SyntaxFactory.AreEquivalent(designations[0], SyntaxFactory.ParseExpression("subject.Designate(target)"));
    }
}
