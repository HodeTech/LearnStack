using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using EnrollmentCensus = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class PersistenceConventionTests
{
    private const string ContextualEnrollment = "LearnStack.Modules.Identity.Infrastructure.Persistence.ContextualIdentityStorageControl";
    private const string ReceiptEnrollment = "LearnStack.Modules.Identity.Infrastructure.Persistence.IdentityReceiptReader";
    private const string TenancyEnrollment = "LearnStack.Modules.Tenancy.Infrastructure.Persistence.TenancyAdministrationReader";
    private const string AdministrationReadEnrollment = "LearnStack.Modules.Identity.Infrastructure.Persistence.IdentityAdministrationReadStore";
    private const string NavigationEnrollment = "LearnStack.Modules.Tenancy.Infrastructure.Persistence.OwnTenantNavigationReader";
    private const string EfEnrollment = "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions";

    private static void AssertExactEfEnrollment()
    {
        using var census = EnrollmentCensus.Read();
        EfEnrollmentOffenders(census).Should().BeEmpty("only the common factory and exact Identity/Tenancy owner reenrollment paths may bind EF to an existing transaction");
        var sites = census.Methods.SelectMany(method => method.Body.Instructions.Where(IsEfEnrollment)
            .Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        var guardedReads = census.Methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == ContextualEnrollment && target.Name == "EnrollCurrentTenantOwner")
            .Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        foreach (var site in sites.Concat(guardedReads))
        {
            var opcode = site.Instruction.OpCode;
            var target = site.Instruction.Operand;
            site.Instruction.OpCode = OpCodes.Nop;
            site.Instruction.Operand = null;
            try { EfEnrollmentOffenders(census).Should().NotBeEmpty("each actual enrollment is a required subject"); }
            finally { site.Instruction.OpCode = opcode; site.Instruction.Operand = target; }
        }
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
        {
            var owner = census.Type(ContextualEnrollment);
            var extra = new MethodDefinition("UnownedEnrollment", MethodAttributes.Private | MethodAttributes.Static, owner.Module.TypeSystem.Void);
            extra.Body.Instructions.Add(Instruction.Create(opcode, (MethodReference)sites[0].Instruction.Operand));
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            owner.Methods.Add(extra);
            try { EfEnrollmentOffenders(census).Should().NotBeEmpty("an extra enrollment or executable alias cannot borrow an approved type"); }
            finally { owner.Methods.Remove(extra); }
        }
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/ContextualIdentityStores.cs"));
        ContextualEnrollmentIsGuarded(source).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            ("!owner.HasActiveTransaction", "false"), ("owner.IsRollbackOnly", "false"),
            ("owner is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }", "false"),
            ("!owner.IsTenantContextIssuedOn(owner.Transaction)", "false"), ("Generation: > 0", "Generation: >= 0"),
            ("announcement.TenantId != context.CurrentTenantId", "false"),
            ("announcement.OrganizationId != context.CurrentOrganizationId", "false"),
            ("!ReferenceEquals(context.Database.GetDbConnection(), owner.Connection)", "false"),
            ("context.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)", "false"),
            ("!ReferenceEquals(current.GetDbTransaction(), owner.Transaction)", "false"),
        })
        {
            var dirty = source.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(source);
            ContextualEnrollmentIsGuarded(dirty).Should().BeFalse("the same connection, fresh announcement, scope, mode and clean graph are required");
        }
        var tenancy = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/TenancyAdministrationReader.cs"));
        TenancyEnrollmentIsGuarded(tenancy).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            ("!context.IsResolved", "false"), ("context.TenantId == TenantId.PlatformSentinel", "false"),
            ("context.UserId is null", "false"), ("context.UserId == UserId.SystemActor", "false"),
            ("!unit.HasActiveTransaction", "false"), ("unit.IsRollbackOnly", "false"),
            ("unit.Mode is not (TransactionMode.ReadOnly or TransactionMode.ReadWrite)", "false"),
            ("unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }", "false"),
            ("!unit.IsTenantContextIssuedOn(unit.Transaction)", "false"), ("Generation: > 0", "Generation: >= 0"),
            ("announcement.TenantId != context.TenantId", "false"),
            ("announcement.OrganizationId != context.OrganizationId", "false"),
            ("db.CurrentTenantId != context.TenantId", "false"), ("db.CurrentOrganizationId != context.OrganizationId", "false"),
            ("!ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)", "false"),
            ("db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)", "false"),
            ("!ReferenceEquals(current.GetDbTransaction(), unit.Transaction)", "false"),
        })
        {
            var dirty = tenancy.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(tenancy);
            TenancyEnrollmentIsGuarded(dirty).Should().BeFalse("Tenancy facts cannot retarget or borrow a stale, dirty or global owner");
        }
        var receipt = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/IdentityReceiptReader.cs"));
        ReceiptEnrollmentIsGuarded(receipt).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            ("!context.IsResolved", "false"), ("context.TenantId == TenantId.PlatformSentinel", "false"),
            ("context.UserId is null", "false"), ("context.UserId == UserId.SystemActor", "false"),
            ("unit.Mode != TransactionMode.ReadWrite", "unit.Mode != TransactionMode.ReadOnly"),
            ("unit is IOperationReadScopeView { IsTenantWideRead: true }", "false"),
            ("db.CurrentTenantWideRead", "false"),
            ("ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);", "return;"),
        })
        {
            var dirty = receipt.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(receipt);
            ReceiptEnrollmentIsGuarded(dirty).Should().BeFalse("the receipt's MUST audit requires the same ordinary scoped writable owner");
        }
        var administration = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/IdentityAdministrationReadStore.cs"));
        AdministrationReadEnrollmentIsGuarded(administration).Should().BeTrue();
        foreach (var before in new[] { "!context.IsResolved", "context.TenantId == TenantId.PlatformSentinel",
                     "context.UserId is null", "context.UserId == UserId.SystemActor", "unit.Mode != TransactionMode.ReadWrite",
                     "unit is IOperationReadScopeView { IsTenantWideRead: true }", "db.CurrentTenantWideRead" })
        {
            var dirty = administration.Replace(before, "false", StringComparison.Ordinal);
            dirty.Should().NotBe(administration);
            AdministrationReadEnrollmentIsGuarded(dirty).Should().BeFalse("MUST administration reads retain their exact ordinary writable owner");
        }
        AdministrationReadEnrollmentIsGuarded(administration.Replace(
            "ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);", "return;", StringComparison.Ordinal)).Should().BeFalse();
        var navigation = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/OwnTenantNavigationReader.cs"));
        NavigationEnrollmentIsGuarded(navigation).Should().BeTrue();
        foreach (var before in new[]
        {
            "!ReferenceEquals(invocations.CurrentInvocation, invocation)", "!invocation.IsActive",
            "invocation.EntryKind != OperationEntryKind.Application", "invocation.TrustedSource is not null",
            "invocation.Policy.Kind != OperationPolicyKind.OwnMembershipRead", "invocation.Policy.Id != OwnTenantNavigationContract.Operation",
            "invocation.Policy.ReadScope != OperationReadScope.Scoped", "invocation.Policy.RequiresOrglessWrite", "invocation.Policy.RequiresOrganization",
            "invocation.Policy.RequestType != invocation.Operation.GetType()", "!ReferenceEquals(invocation.Context, context)",
            "!ReferenceEquals(decision.Invocation, invocation)", "!context.IsResolved", "context.TenantId == TenantId.PlatformSentinel",
            "context.UserId is null", "context.UserId == UserId.SystemActor", "candidate.Actor != context.UserId",
            "candidate.TenantId != context.TenantId", "candidate.OrganizationId != context.OrganizationId",
            "invocation.Actor != context.UserId", "invocation.TenantId != context.TenantId", "invocation.OrganizationId != context.OrganizationId",
            "(!access.HasTenantMembership && context.OrganizationId is null)", "!unit.HasActiveTransaction", "unit.IsRollbackOnly",
            "unit.Mode != TransactionMode.ReadOnly", "unit is IOperationReadScopeView { IsTenantWideRead: true }",
            "unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }", "!unit.IsTenantContextIssuedOn(unit.Transaction)",
            "announcement.TenantId != context.TenantId", "announcement.OrganizationId != context.OrganizationId",
            "db.CurrentTenantId != context.TenantId", "db.CurrentOrganizationId != context.OrganizationId", "db.CurrentTenantWideRead",
            "!ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)",
            "db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)",
            "!ReferenceEquals(current.GetDbTransaction(), unit.Transaction)",
        })
        {
            var dirty = navigation.Replace(before, "false", StringComparison.Ordinal);
            dirty.Should().NotBe(navigation);
            NavigationEnrollmentIsGuarded(dirty).Should().BeFalse("own navigation cannot borrow another caller, candidate, scope or physical owner");
        }
        foreach (var (before, after) in new[]
        {
            ("Generation: > 0", "Generation: >= 0"), ("IsActive: true", "IsActive: false"),
            ("Outcome: ProtectedOperationOutcome.Allowed", "Outcome: ProtectedOperationOutcome.Forbidden"),
            ("SourceKind: AuthorizationSourceKind.Http", "SourceKind: AuthorizationSourceKind.TrustedOperation"),
            ("db.Database.UseTransaction(unit.Transaction);", "return true;"),
        })
        {
            var dirty = navigation.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(navigation);
            NavigationEnrollmentIsGuarded(dirty).Should().BeFalse("the same source guard must reject weaker live evidence or missing reenrollment");
        }
        EfEnrollmentOffenders(census).Should().BeEmpty("all actual compiled enrollment subjects are restored");
    }

    private static List<string> EfEnrollmentOffenders(EnrollmentCensus census)
    {
        var sites = census.Methods.SelectMany(method => method.Body.Instructions.Where(IsEfEnrollment)
            .Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        var actual = sites.Select(site => site.Method.DeclaringType.DeclaringType?.FullName == "LearnStack.Infrastructure.Persistence.ModuleDbContextRegistration"
                && site.Method.Name.StartsWith("<AddModuleDbContext>", StringComparison.Ordinal)
            ? "LearnStack.Infrastructure.Persistence.ModuleDbContextRegistration::AddModuleDbContext" : census.Owner(site.Method)).Order(StringComparer.Ordinal);
        var expected = new[] { "LearnStack.Infrastructure.Persistence.ModuleDbContextRegistration::AddModuleDbContext",
            "LearnStack.Modules.Identity.Infrastructure.Persistence.UserBindingStore::EnrollCurrentOwner",
            ContextualEnrollment + "::EnrollCurrentTenantOwner",
            TenancyEnrollment + "::Admit", NavigationEnrollment + "::Matches" }.Order(StringComparer.Ordinal);
        var errors = new List<string>();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal) || sites.Any(site => site.Instruction.OpCode != OpCodes.Call
            || site.Instruction.Operand is not MethodReference target
            || target.FullName != "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions::UseTransaction(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.Data.Common.DbTransaction)"))
            errors.Add("EF enrollment callers/signatures must remain the exact common factory, global binding owner and guarded contextual/Tenancy reenrollment");
        var reenrollments = census.Methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == ContextualEnrollment && target.Name == "EnrollCurrentTenantOwner")
            .Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        if (!reenrollments.Select(site => census.Owner(site.Method)).Order(StringComparer.Ordinal).SequenceEqual(new[]
            { ContextualEnrollment + "::PrepareFreshRead", ContextualEnrollment + "::LockRootAsync",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.MembershipDirectoryReader::Admit",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.MembershipAdministrationReader::Admit",
                ReceiptEnrollment + "::Admit",
                AdministrationReadEnrollment + "::Admit",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.RoleReadStore::ReadAsync",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.RoleReadStore::ReadManyAsync",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.MembershipAdmissionReader::ReadCurrentAsync",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.SeedStateReader::ReadRoleCoreAsync",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.SeedStateReader::ReadMembershipCoreAsync",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.FixedSeedIdentityPrerequisiteReader::ReadAsync",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.FixedSeedIdentityCompletionReader::ReadAsync",
                "LearnStack.Modules.Identity.Infrastructure.Persistence.SeedIdentityStageReader::HasNoRoleHistoryAsync" }.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || reenrollments.Any(site => site.Instruction.OpCode != OpCodes.Call))
            errors.Add("Only the exact contextual graph, seed metadata/stage, member/Role and native admission readers may reenroll the same tenant owner");
        return errors;
    }

    private static bool ReceiptEnrollmentIsGuarded(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var methods = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "IdentityReceiptReader")
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()).Where(method => method.Identifier.ValueText == "Admit").ToArray();
        if (root.ContainsDiagnostics || methods.Length != 1 || methods[0].Modifiers.Count != 1
            || !methods[0].Modifiers.Any(SyntaxKind.PrivateKeyword) || methods[0].ReturnType.ToString() != "void"
            || methods[0].ParameterList.Parameters.Count != 0 || methods[0].Body is not { Statements.Count: 2 } body
            || body.Statements[0] is not IfStatementSyntax guard) return false;
        return SyntaxFactory.AreEquivalent(guard.Condition, SyntaxFactory.ParseExpression("""
            !context.IsResolved || context.TenantId == TenantId.PlatformSentinel
            || context.UserId is null || context.UserId == UserId.SystemActor
            || unit.Mode != TransactionMode.ReadWrite || unit is IOperationReadScopeView { IsTenantWideRead: true }
            || db.CurrentTenantWideRead
            """)) && guard.Statement is ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax exception }
            && exception.Type.ToString() == "TenantContextMissingException"
            && SyntaxFactory.AreEquivalent(body.Statements[1], SyntaxFactory.ParseStatement(
                "ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);"));
    }

    private static bool AdministrationReadEnrollmentIsGuarded(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var methods = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "IdentityAdministrationReadStore")
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()).Where(method => method.Identifier.ValueText == "Admit").ToArray();
        if (root.ContainsDiagnostics || methods.Length != 1 || methods[0].Modifiers.Count != 1
            || !methods[0].Modifiers.Any(SyntaxKind.PrivateKeyword) || methods[0].ReturnType.ToString() != "void"
            || methods[0].ParameterList.Parameters.Count != 0 || methods[0].Body is not { Statements.Count: 2 } body
            || body.Statements[0] is not IfStatementSyntax guard) return false;
        return SyntaxFactory.AreEquivalent(guard.Condition, SyntaxFactory.ParseExpression("""
            !context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.UserId is null
            || context.UserId == UserId.SystemActor || unit.Mode != TransactionMode.ReadWrite
            || unit is IOperationReadScopeView { IsTenantWideRead: true } || db.CurrentTenantWideRead
            """)) && guard.Statement is ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax exception }
            && exception.Type.ToString() == "TenantContextMissingException"
            && SyntaxFactory.AreEquivalent(body.Statements[1], SyntaxFactory.ParseStatement(
                "ContextualIdentityStorageControl.EnrollCurrentTenantOwner(db, unit);"));
    }

    private static bool NavigationEnrollmentIsGuarded(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var methods = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "OwnTenantNavigationReader")
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()).Where(method => method.Identifier.ValueText == "Matches").ToArray();
        if (root.ContainsDiagnostics || methods.Length != 1 || methods[0].Modifiers.Count != 1
            || !methods[0].Modifiers.Any(SyntaxKind.PrivateKeyword) || methods[0].ReturnType.ToString() != "bool"
            || !SyntaxFactory.AreEquivalent(methods[0].ParameterList, SyntaxFactory.ParseParameterList("(OwnTenantNavigationAccess access)"))
            || methods[0].Body is not { Statements.Count: 4 } body
            || !SyntaxFactory.AreEquivalent(body.Statements[0], SyntaxFactory.ParseStatement("var invocation = access.Invocation;"))
            || body.Statements[1] is not IfStatementSyntax guard || body.Statements[2] is not IfStatementSyntax current
            || !SyntaxFactory.AreEquivalent(body.Statements[3], SyntaxFactory.ParseStatement("return true;"))) return false;
        return SyntaxFactory.AreEquivalent(guard.Condition, SyntaxFactory.ParseExpression("""
            !ReferenceEquals(invocations.CurrentInvocation, invocation) || !invocation.IsActive
            || invocation.EntryKind != OperationEntryKind.Application || invocation.TrustedSource is not null
            || invocation.Policy.Kind != OperationPolicyKind.OwnMembershipRead
            || invocation.Policy.Id != OwnTenantNavigationContract.Operation
            || invocation.Policy.ReadScope != OperationReadScope.Scoped || invocation.Policy.RequiresOrglessWrite
            || invocation.Policy.RequiresOrganization
            || invocation.Policy.RequestType != invocation.Operation.GetType() || !ReferenceEquals(invocation.Context, context)
            || invocations.CurrentDecision is not { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } decision
            || !ReferenceEquals(decision.Invocation, invocation)
            || invocation.Candidate is not { IsActive: true, SourceKind: AuthorizationSourceKind.Http } candidate
            || !context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.UserId is null
            || context.UserId == UserId.SystemActor || candidate.Actor != context.UserId || candidate.TenantId != context.TenantId
            || candidate.OrganizationId != context.OrganizationId || invocation.Actor != context.UserId
            || invocation.TenantId != context.TenantId || invocation.OrganizationId != context.OrganizationId
            || (!access.HasTenantMembership && context.OrganizationId is null)
            || !unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadOnly
            || unit is IOperationReadScopeView { IsTenantWideRead: true }
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != context.TenantId || announcement.OrganizationId != context.OrganizationId
            || db.CurrentTenantId != context.TenantId || db.CurrentOrganizationId != context.OrganizationId || db.CurrentTenantWideRead
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            """)) && SyntaxFactory.AreEquivalent(guard.Statement, SyntaxFactory.ParseStatement("return false;"))
            && SyntaxFactory.AreEquivalent(current.Condition, SyntaxFactory.ParseExpression(
                "db.Database.CurrentTransaction is not { } current || !ReferenceEquals(current.GetDbTransaction(), unit.Transaction)"))
            && SyntaxFactory.AreEquivalent(current.Statement, SyntaxFactory.ParseStatement("db.Database.UseTransaction(unit.Transaction);"));
    }

    private static bool TenancyEnrollmentIsGuarded(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var methods = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "TenancyAdministrationReader")
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()).Where(method => method.Identifier.ValueText == "Admit").ToArray();
        if (root.ContainsDiagnostics || methods.Length != 1 || methods[0].Modifiers.Count != 1
            || !methods[0].Modifiers.Any(SyntaxKind.PrivateKeyword) || methods[0].ReturnType.ToString() != "void"
            || methods[0].ParameterList.Parameters.Count != 0
            || methods[0].Body is not { Statements.Count: 2 } body || body.Statements[0] is not IfStatementSyntax guard
            || body.Statements[1] is not IfStatementSyntax current)
            return false;
        return SyntaxFactory.AreEquivalent(guard.Condition, SyntaxFactory.ParseExpression("""
            !context.IsResolved || context.TenantId == TenantId.PlatformSentinel || context.UserId is null
            || context.UserId == UserId.SystemActor || !unit.HasActiveTransaction || unit.IsRollbackOnly
            || unit.Mode is not (TransactionMode.ReadOnly or TransactionMode.ReadWrite)
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || !unit.IsTenantContextIssuedOn(unit.Transaction)
            || unit.GetTenantContextAnnouncement(unit.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != context.TenantId || announcement.OrganizationId != context.OrganizationId
            || db.CurrentTenantId != context.TenantId || db.CurrentOrganizationId != context.OrganizationId
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            """)) && guard.Statement is ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax exception }
            && exception.Type.ToString() == "TenantContextMissingException"
            && SyntaxFactory.AreEquivalent(current.Condition, SyntaxFactory.ParseExpression("db.Database.CurrentTransaction is not { } current || !ReferenceEquals(current.GetDbTransaction(), unit.Transaction)"))
            && SyntaxFactory.AreEquivalent(current.Statement, SyntaxFactory.ParseStatement("db.Database.UseTransaction(unit.Transaction);"));
    }

    private static bool IsEfEnrollment(Instruction instruction) => instruction.Operand is MethodReference target
        && target.DeclaringType.FullName == EfEnrollment && target.Name is "UseTransaction" or "UseTransactionAsync";

    private static bool ContextualEnrollmentIsGuarded(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var methods = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "ContextualIdentityStorageControl")
            .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>()).Where(method => method.Identifier.ValueText == "EnrollCurrentTenantOwner").ToArray();
        if (root.ContainsDiagnostics || methods.Length != 1 || methods[0].Modifiers.Count != 2
            || !methods[0].Modifiers.Any(SyntaxKind.InternalKeyword) || !methods[0].Modifiers.Any(SyntaxKind.StaticKeyword)
            || methods[0].ReturnType.ToString() != "void"
            || !SyntaxFactory.AreEquivalent(methods[0].ParameterList, SyntaxFactory.ParseParameterList("(IdentityDbContext context, IUnitOfWork owner)"))
            || methods[0].Body is not { Statements.Count: 2 } body || body.Statements[0] is not IfStatementSyntax guard
            || body.Statements[1] is not IfStatementSyntax current)
            return false;
        return SyntaxFactory.AreEquivalent(guard.Condition, SyntaxFactory.ParseExpression("""
            !owner.HasActiveTransaction || owner.IsRollbackOnly
            || owner is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || !owner.IsTenantContextIssuedOn(owner.Transaction)
            || owner.GetTenantContextAnnouncement(owner.Transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != context.CurrentTenantId || announcement.OrganizationId != context.CurrentOrganizationId
            || !ReferenceEquals(context.Database.GetDbConnection(), owner.Connection)
            || context.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            """)) && guard.Statement is ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax exception }
            && exception.Type.ToString() == "TenantContextMissingException"
            && SyntaxFactory.AreEquivalent(current.Condition, SyntaxFactory.ParseExpression("context.Database.CurrentTransaction is not { } current || !ReferenceEquals(current.GetDbTransaction(), owner.Transaction)"))
            && SyntaxFactory.AreEquivalent(current.Statement, SyntaxFactory.ParseStatement("context.Database.UseTransaction(owner.Transaction);"));
    }
}
