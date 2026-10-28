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
    private const string EfEnrollment = "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions";

    private static void AssertExactEfEnrollment()
    {
        using var census = EnrollmentCensus.Read();
        EfEnrollmentOffenders(census).Should().BeEmpty("only the common factory and exact Identity owner reenrollment paths may bind EF to an existing transaction");
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
            ContextualEnrollment + "::EnrollCurrentTenantOwner" }.Order(StringComparer.Ordinal);
        var errors = new List<string>();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal) || sites.Any(site => site.Instruction.OpCode != OpCodes.Call
            || site.Instruction.Operand is not MethodReference target
            || target.FullName != "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions::UseTransaction(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade,System.Data.Common.DbTransaction)"))
            errors.Add("EF enrollment callers/signatures must remain the exact common factory, global binding owner and guarded contextual reenrollment");
        var reenrollments = census.Methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == ContextualEnrollment && target.Name == "EnrollCurrentTenantOwner")
            .Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        if (!reenrollments.Select(site => census.Owner(site.Method)).Order(StringComparer.Ordinal).SequenceEqual(new[]
            { ContextualEnrollment + "::PrepareFreshRead", "LearnStack.Modules.Identity.Infrastructure.Persistence.MembershipDirectoryReader::Admit" }.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || reenrollments.Any(site => site.Instruction.OpCode != OpCodes.Call))
            errors.Add("Only fresh contextual graph reads and the guarded directory may reenroll the same tenant owner");
        return errors;
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
