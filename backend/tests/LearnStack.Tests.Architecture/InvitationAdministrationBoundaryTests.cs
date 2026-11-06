using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0093's actual nonempty production writers and current-root status consumers.</summary>
public sealed class InvitationAdministrationBoundaryTests
{
    private const string Handler = "LearnStack.Modules.Identity.Application.Invitations.InvitationAdministrationHandlers";
    private const string Stager = "LearnStack.Modules.Identity.Application.Invitations.IInvitationEmailStager";
    private const string Status = "LearnStack.SharedKernel.Email.IInvitationEmailStatusReader";
    private const string Contracts = "LearnStack.Modules.Identity.Application.Contracts.Invitations.";
    private const string Create = Contracts + "CreateInvitationCommand";
    private const string Reissue = Contracts + "ReissueInvitationCommand";
    private const string Revoke = Contracts + "RevokeInvitationCommand";
    private const string List = Contracts + "ListInvitationsQuery";
    private const string Get = Contracts + "GetInvitationQuery";

    [Fact]
    public void Invitation_production_staging_and_status_have_exact_nonempty_callers()
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try
        {
            var types = modules.SelectMany(module => module.Types.SelectMany(Flatten)).ToArray();
            Offenders(types).Should().BeEmpty("Fix: create/reissue use the actual stager, and only current authorized List/Get roots read exact-job status");
            foreach (var (port, method) in new[] { (Stager, "StageAsync"), (Status, "ReadAsync"), (Status, "ReadManyAsync") })
            {
                var site = Sites(types, port, method).Single();
                var opcode = site.Instruction.OpCode;
                foreach (var dirty in new[] { OpCodes.Nop, OpCodes.Ldftn })
                {
                    site.Instruction.OpCode = dirty;
                    try { Offenders(types).Should().NotBeEmpty("missing calls and executable aliases cannot preserve the production path"); }
                    finally { site.Instruction.OpCode = opcode; }
                }
                var owner = types.Single(type => type.FullName == Handler);
                var extra = new MethodDefinition("AdditionalInvitationConsumer", MethodAttributes.Private | MethodAttributes.Static, owner.Module.TypeSystem.Void);
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, (MethodReference)site.Instruction.Operand));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                owner.Methods.Add(extra);
                try { Offenders(types).Should().NotBeEmpty("another consumer cannot hide in the sanctioned handler type"); }
                finally { owner.Methods.Remove(extra); }
            }
            var handler = types.Single(type => type.FullName == Handler);
            foreach (var request in new[] { Create, Reissue, List, Get })
            {
                var contract = handler.Interfaces.Single(item => Handles(item.InterfaceType, request));
                handler.Interfaces.Remove(contract);
                try { Offenders(types).Should().NotBeEmpty("the real IRequestHandler root must remain present"); }
                finally { handler.Interfaces.Add(contract); }
            }
            foreach (var site in Sites(types, Handler, "MutateAsync").Concat(Sites(types, Handler, "Project")).ToArray())
            {
                var opcode = site.Instruction.OpCode;
                site.Instruction.OpCode = OpCodes.Nop;
                try { Offenders(types).Should().NotBeEmpty("every actual public writer/read root must retain its helper dispatch"); }
                finally { site.Instruction.OpCode = opcode; }
            }
            Offenders(types).Should().BeEmpty("all production subjects restored");
        }
        finally { foreach (var module in modules) module.Dispose(); }

        var staging = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Invitations/InvitationEmailStager.cs"));
        var handlers = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Identity/LearnStack.Modules.Identity.Application/Invitations/InvitationAdministrationHandlers.cs"));
        SourceIsBound(staging, handlers).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            (Create, Revoke), (Reissue, Revoke),
            ("typeof(InvitationEmailParameters).Assembly", "typeof(object).Assembly"),
            ("producer.CaptureJobOrigin(unit)", "producer.CaptureJobOrigin(default!)"),
            ("producer.RequireJobOrigin(origin, unit)", "producer.RequireJobOrigin(origin, default!)"),
        })
        {
            var dirty = staging.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(staging);
            SourceIsBound(dirty, handlers).Should().BeFalse("only the two exact Contracts writers can stage production mail");
        }
        foreach (var replacement in new[] { "default", "target.Id" })
        {
            var dirty = handlers.Replace("status.ReadAsync(target.CurrentDeliveryJobId, cancellationToken)",
                "status.ReadAsync(" + replacement + ", cancellationToken)", StringComparison.Ordinal);
            dirty.Should().NotBe(handlers);
            SourceIsBound(staging, dirty).Should().BeFalse("the status selector is the loaded root's actual current job, never a supplied or substitute ID");
        }
        // The page read is bound the same way: only the loaded rows' own current jobs, once, with the page budget.
        foreach (var replacement in new[] { "row => default", "row => row.Id", "_ => Guid.Empty" })
        {
            var dirty = handlers.Replace("row => row.CurrentDeliveryJobId", replacement, StringComparison.Ordinal);
            dirty.Should().NotBe(handlers);
            SourceIsBound(staging, dirty).Should().BeFalse("the page selectors are the loaded rows' actual current jobs, never supplied or substitute IDs");
        }
        var unbudgeted = handlers.Replace("ToArray(), pageBudget.Token)", "ToArray(), cancellationToken)", StringComparison.Ordinal);
        unbudgeted.Should().NotBe(handlers);
        SourceIsBound(staging, unbudgeted).Should().BeFalse("the page status read keeps its own finite budget");
    }

    private static string[] Offenders(TypeDefinition[] types)
    {
        var errors = new List<string>();
        foreach (var (port, implementation) in new[]
        {
            (Stager, "LearnStack.Modules.Identity.Infrastructure.Invitations.InvitationEmailStager"),
            (Status, "LearnStack.Infrastructure.Email.InvitationEmailStatusReader"),
        })
        {
            var adapters = types.Where(type => !type.IsAbstract && type.Interfaces.Any(item => item.InterfaceType.FullName == port)).ToArray();
            if (adapters.Length != 1 || adapters[0].FullName != implementation) errors.Add("Exact invitation adapter missing or expanded: " + port);
        }
        foreach (var request in new[] { Create, Reissue, Revoke, List, Get })
        {
            var roots = types.Where(type => !type.IsAbstract && type.Interfaces.Any(item => Handles(item.InterfaceType, request))).ToArray();
            if (roots.Length != 1 || roots[0].FullName != Handler) errors.Add("Exact invitation root missing or expanded: " + request);
        }
        Check(Stager, "StageAsync", [Handler + "::MutateAsync(System.Object)"], OpCodes.Callvirt);
        Check(Status, "ReadAsync", [Root(Get)], OpCodes.Callvirt);
        Check(Status, "ReadManyAsync", [Root(List)], OpCodes.Callvirt);
        Check(Handler, "MutateAsync", [Root(Create), Root(Reissue), Root(Revoke)], OpCodes.Call);
        Check(Handler, "Project", [Root(List), Root(Get)], OpCodes.Call);
        return errors.ToArray();

        static string Root(string request) => Handler + "::Handle(" + request + ")";
        void Check(string port, string method, string[] expected, OpCode opcode)
        {
            var actual = Sites(types, port, method).ToArray();
            if (!actual.Select(site => Key(site.Owner)).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || actual.Any(site => site.Instruction.OpCode != opcode)) errors.Add("Invitation caller boundary changed: " + port + "::" + method);
        }
    }

    private static bool Handles(TypeReference type, string request) => type is GenericInstanceType handler
        && handler.ElementType.FullName == "MediatR.IRequestHandler`2" && handler.GenericArguments.Count == 2
        && handler.GenericArguments[0].FullName == request;

    private static IEnumerable<(MethodDefinition Owner, Instruction Instruction)> Sites(TypeDefinition[] types, string port, string method)
    {
        var owners = types.SelectMany(type => type.Methods).SelectMany(owner => owner.CustomAttributes
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
            .Select(attribute => (State: ((TypeReference)attribute.ConstructorArguments[0].Value).FullName, Owner: owner)))
            .ToDictionary(pair => pair.State, pair => pair.Owner, StringComparer.Ordinal);
        return types.SelectMany(type => type.Methods).Where(body => body.HasBody).SelectMany(body => body.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference call && call.DeclaringType.FullName == port && call.Name == method)
            .Select(instruction => (owners.GetValueOrDefault(body.DeclaringType.FullName, body), instruction)));
    }
    private static string Key(MethodDefinition method) => method.DeclaringType.FullName + "::" + method.Name
        + "(" + (method.Parameters.Count == 0 ? string.Empty : method.Parameters[0].ParameterType.FullName) + ")";
    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));

    private static bool SourceIsBound(string staging, string handlers)
    {
        var stager = CSharpSyntaxTree.ParseText(staging).GetRoot();
        var owner = CSharpSyntaxTree.ParseText(handlers).GetRoot();
        if (stager.ContainsDiagnostics || owner.ContainsDiagnostics) return false;
        var whitelist = stager.DescendantNodes().OfType<IsPatternExpressionSyntax>()
            .Where(pattern => pattern.Expression.ToString() == "origin.OperationType.FullName").ToArray();
        var assembly = stager.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(expression => expression.IsKind(SyntaxKind.NotEqualsExpression)
            && expression.Left.ToString() == "origin.OperationType.Assembly" && expression.Right.ToString() == "typeof(InvitationEmailParameters).Assembly").ToArray();
        var reads = owner.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression.ToString() == "status.ReadAsync").ToArray();
        var pageReads = owner.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression.ToString() == "status.ReadManyAsync").ToArray();
        static string HandledRequest(SyntaxNode node) => node.Ancestors().OfType<MethodDeclarationSyntax>().First()
            .ParameterList.Parameters[0].Type!.ToString();
        var capture = stager.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression.ToString() == "producer.CaptureJobOrigin").ToArray();
        var recheck = stager.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression.ToString() == "producer.RequireJobOrigin").ToArray();
        return whitelist.Length == 1 && SyntaxFactory.AreEquivalent(whitelist[0], SyntaxFactory.ParseExpression(
                "origin.OperationType.FullName is not (\"" + Create + "\" or \"" + Reissue + "\")"))
            && assembly.Length == 1 && capture.Length == 1 && recheck.Length == 1
            && SyntaxFactory.AreEquivalent(capture[0], SyntaxFactory.ParseExpression("producer.CaptureJobOrigin(unit)"))
            && SyntaxFactory.AreEquivalent(recheck[0], SyntaxFactory.ParseExpression("producer.RequireJobOrigin(origin, unit)"))
            && reads.Length == 1
            && reads[0].Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText == "Handle"
            && HandledRequest(reads[0]) == "GetInvitationQuery"
            && SyntaxFactory.AreEquivalent(reads[0], SyntaxFactory.ParseExpression("status.ReadAsync(target.CurrentDeliveryJobId, cancellationToken)"))
            && pageReads.Length == 1
            && pageReads[0].Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText == "Handle"
            && HandledRequest(pageReads[0]) == "ListInvitationsQuery"
            && SyntaxFactory.AreEquivalent(pageReads[0], SyntaxFactory.ParseExpression(
                "status.ReadManyAsync(page.Select(row => row.CurrentDeliveryJobId).ToArray(), pageBudget.Token)"));
    }
}
