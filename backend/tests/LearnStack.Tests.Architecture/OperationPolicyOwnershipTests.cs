using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Census = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

/// <summary>Operation authority is constructed only by the exact policy/evaluator/physical owner chain.</summary>
public sealed partial class OperationPolicyOwnershipTests
{
    private const string Permissions = "LearnStack.SharedKernel.Permissions.";
    private const string Invocation = Permissions + "OperationInvocation";
    private const string Decision = Permissions + "ProtectedOperationDecision";
    private const string Witness = Permissions + "OperationReadWitness";
    private const string Candidate = Permissions + "AuthorizationCandidate";
    private const string Coordinator = "LearnStack.Application.Authorization.OperationAuthorizationCoordinator";
    private const string Frame = Coordinator + "/Frame";
    private const string Evaluator = "LearnStack.Modules.Identity.Application.Authorization.IdentityOperationAuthorizer";
    private const string RoleEvaluator = "LearnStack.Modules.Identity.Application.Authorization.RoleGrantAuthorization";
    private const string MemberEvaluator = "LearnStack.Modules.Identity.Application.Authorization.MembershipAdministrationAuthorization";
    private const string TenancyEvaluator = "LearnStack.Modules.Tenancy.Application.Authorization.TenancyAdministrationAuthorization";
    private static readonly (string Type, string Method, string Target, string Name)[] MemberChecks =
    [
        (MemberEvaluator, "Matches", Invocation, "get_IsActive"),
        (MemberEvaluator, "Matches", Candidate, "get_IsActive"),
        (MemberEvaluator, "Matches", Decision, "get_IsActive"),
        (MemberEvaluator, "Matches", Permissions + "IOperationPolicyCatalog", "TryGet"),
        (MemberEvaluator, "RecheckAsync", "LearnStack.Modules.Identity.Application.Contracts.Authorization.IIdentityAuthorizationReader", "ReadAsync"),
        (MemberEvaluator, "RecheckAsync", "LearnStack.Modules.Identity.Application.Authorization.IdentityPolicyEvaluation", "Evaluate"),
    ];
    private static readonly (string Type, string Method, string Target, string Name)[] TenancyChecks =
    [
        (TenancyEvaluator, "Matches", Invocation, "get_IsActive"),
        (TenancyEvaluator, "Matches", Candidate, "get_IsActive"),
        (TenancyEvaluator, "Matches", Decision, "get_IsActive"),
        (TenancyEvaluator, "Matches", Permissions + "IOperationPolicyCatalog", "TryGet"),
        (TenancyEvaluator, "RecheckAsync", Permissions + "IProtectedOperationAuthorizer", "AuthorizeAsync"),
        (TenancyEvaluator, "RecheckAsync", Invocation, "get_Policy"),
        (TenancyEvaluator, "RecheckAsync", Invocation, "get_Candidate"),
        (TenancyEvaluator, "VerifyWriteAsync", TenancyEvaluator, "HasCurrentTenantAsync"),
        (TenancyEvaluator, "VerifyWriteAsync", TenancyEvaluator, "RecheckAsync"),
    ];
    private const string HttpSource = "LearnStack.Api.Authentication.TenantAuthorizationCandidateSource";
    private const string Unit = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
    private const string AuthorizationBehavior = "LearnStack.Application.Pipeline.AuthorizationBehavior`2";

    [Fact]
    public void Protected_Operations_Require_Explicit_Live_Policies()
    {
        AssertLiveComposition();
        AssertTenancyCanonicalRecheck();
        using var census = Census.Read();
        Offenders(census).Should().BeEmpty("Fix: preserve the exact invocation/evaluator/read-owner authority chain and lifetime (ADR-0076)");
        foreach (var typeName in new[] { Invocation, Decision, Witness })
        {
            var type = census.Type(typeName);
            Dirty(census, () =>
            {
                type.IsSealed = false;
                return () => type.IsSealed = true;
            });
            Dirty(census, () =>
            {
                var constructor = type.Methods.Single(method => method.IsConstructor);
                var attributes = constructor.Attributes;
                constructor.IsPublic = true;
                return () => constructor.Attributes = attributes;
            });
            Dirty(census, () =>
            {
                var field = type.Fields.First(field => field.IsInitOnly);
                var attributes = field.Attributes;
                field.IsInitOnly = false;
                return () => field.Attributes = attributes;
            });
            Dirty(census, () =>
            {
                var module = census.Type(Coordinator).Module;
                var extra = new TypeDefinition("AuthorityControl", "Extra" + type.Name + "Minter", TypeAttributes.NotPublic,
                    module.ImportReference(census.Type(typeName + "/MintingSource")));
                module.Types.Add(extra);
                return () => module.Types.Remove(extra);
            });
        }
        foreach (var (type, method, target, name) in new[]
        {
            (Invocation, "get_IsActive", Invocation + "/MintingSource", "MatchesCurrent"),
            (Invocation, "get_IsActive", Candidate, "get_IsActive"),
            (Invocation, "get_IsActive", Permissions + "TrustedOperationDescriptor", "get_IsActive"),
            (Invocation, "get_IsActive", "System.Threading.CancellationToken", "get_IsCancellationRequested"),
            (Invocation, "TryBindRead", Invocation, "get_IsActive"),
            (Invocation, "IsReadBoundTo", Invocation, "get_IsActive"),
            (Invocation, "IsReadBoundTo", Invocation + "/ReadBinding", "get_Unit"),
            (Invocation, "IsReadBoundTo", Invocation + "/ReadBinding", "get_Transaction"),
            (Invocation, "IsReadBoundTo", Invocation + "/ReadBinding", "get_Generation"),
            (Witness, "get_IsActive", Invocation, "get_IsActive"),
            (Witness, "get_IsActive", Candidate, "get_IsActive"),
            (Decision, "get_IsActive", Invocation, "get_IsActive"),
            (Witness, "TryBindTo", Invocation, "TryBindRead"),
            (Witness, "IsBoundTo", Invocation, "IsReadBoundTo"),
            (Coordinator, "Dispose", "System.Threading.Interlocked", "Exchange"),
            (Coordinator, "IsCurrent", Frame, "get_Closed"),
            (Coordinator, "ExecuteAsync", Frame, "Close"),
            (Frame, "get_Closed", "System.Threading.Volatile", "Read"),
            (Frame, "Close", "System.Threading.Interlocked", "Exchange"),
            (Evaluator, "AuthorizeAsync", "LearnStack.Modules.Identity.Application.Contracts.Authorization.IIdentityAuthorizationReader", "ReadAsync"),
            (RoleEvaluator, "RecheckAsync", "LearnStack.Modules.Identity.Application.Contracts.Authorization.IIdentityAuthorizationReader", "ReadAsync"),
            ("LearnStack.Api.Authentication.OperationAuthorizationHandler", "HandleAsync", Coordinator, "CheckHttpAsync"),
            ("LearnStack.Api.Authentication.OperationClassificationMiddleware", "InvokeAsync", Coordinator, "CheckHttpAsync"),
            ("LearnStack.Api.Authentication.LiveTenantMembershipReader", "CoversAsync", HttpSource, "ForValidatedScope"),
        }) Dirty(census, () => RemoveCall(census.Body(type, method), target, name));
        foreach (var (type, method, target, name) in MemberChecks.Concat(TenancyChecks))
            Dirty(census, () => RemoveCall(census.Body(type, method), target, name));
        var bind = census.Body(Invocation, "TryBindRead");
        Dirty(census, () => RemoveInstruction(bind.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Stfld
            && instruction.Operand is FieldReference field && field.Name == "_readBinding")));
        var closed = census.Body(Frame, "Close").Body.Instructions.Single(instruction =>
            IsCall(instruction, "System.Threading.Interlocked", "Exchange"));
        Dirty(census, () => Replace(closed.Previous, OpCodes.Ldc_I4_0, null));
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn }) Dirty(census, () =>
        {
            var type = census.Type(Coordinator);
            var method = new MethodDefinition("UnownedReadBinding", MethodAttributes.Private | MethodAttributes.Static, type.Module.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(opcode, type.Module.ImportReference(census.Method(Witness, "TryBindTo"))));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
            return () => type.Methods.Remove(method);
        });
    }

    private static List<string> Offenders(Census census)
    {
        var errors = new List<string>();
        foreach (var name in new[] { Invocation, Decision, Witness })
        {
            var type = census.Type(name);
            if (!type.IsPublic || !type.IsSealed || type.Methods.Count(method => method.IsConstructor) != 1
                || !type.Methods.Single(method => method.IsConstructor).IsPrivate
                || type.Properties.Any(property => property.SetMethod is not null)
                || type.Fields.Any(field => !field.IsPrivate || field.IsStatic || (!field.IsInitOnly &&
                    !(name == Invocation && field.Name == "_readBinding"))))
                errors.Add("Private immutable operation authority shape changed: " + name);
        }
        Derivers(Invocation + "/MintingSource", [Coordinator]);
        Derivers(Decision + "/MintingSource", [Evaluator, RoleEvaluator, MemberEvaluator]);
        Derivers(Witness + "/MintingSource", [Decision + "/MintingSource"]);
        Derivers(Candidate + "/MintingSource", [HttpSource]);
        Implementations(Permissions + "IProtectedOperationAuthorizer", [Evaluator]);
        Implementations(Permissions + "IAuthorizationCandidateSource", [HttpSource]);
        Implementations("Microsoft.AspNetCore.Authorization.IAuthorizationHandler", ["LearnStack.Api.Authentication.OperationAuthorizationHandler"]);
        Exact(Invocation, ".ctor", [(Invocation + "/MintingSource", "ForInvocation", 1)]);
        Exact(Decision, ".ctor", [(Decision + "/MintingSource", "ForDecision", 1)]);
        Exact(Witness, ".ctor", [(Witness + "/MintingSource", "ForTenantRead", 1)]);
        Exact(Invocation + "/MintingSource", "ForInvocation", [(Coordinator, "ExecuteAsync", 1)]);
        Exact(Decision + "/MintingSource", "ForDecision", [(Evaluator, "AuthorizeAsync", 6),
            (RoleEvaluator, "RecheckAsync", 2), (MemberEvaluator, "RecheckAsync", 2)]);
        Exact(Witness + "/MintingSource", "ForTenantRead", [(Decision + "/MintingSource", "ForDecision", 1)]);
        Exact(Permissions + "IProtectedOperationAuthorizer", "AuthorizeAsync", [(Coordinator, "ExecuteAsync", 1),
            (TenancyEvaluator, "RecheckAsync", 1)]);
        Exact(Permissions + "IAuthorizationCandidateSource", "ForValidatedScope", [(Coordinator, "ExecuteAsync", 1)]);
        Exact(Coordinator, "ExecuteRequestAsync", [(AuthorizationBehavior, "Handle", 1)]);
        Exact(HttpSource, "ForValidatedScope", [(HttpSource, Permissions + "IAuthorizationCandidateSource.ForValidatedScope", 1),
            ("LearnStack.Api.Authentication.ConfirmedClaimAssertionSource", "Capture", 1),
            ("LearnStack.Api.Authentication.LiveTenantMembershipReader", "CoversAsync", 1)]);
        Exact(Coordinator, "CheckHttpAsync", [("LearnStack.Api.Authentication.OperationAuthorizationHandler", "HandleAsync", 1),
            ("LearnStack.Api.Authentication.OperationClassificationMiddleware", "InvokeAsync", 1)]);
        Exact(Witness, "TryBindTo", [(Unit, "SetTenantContextCoreAsync", 2)]);
        Exact(Witness, "IsBoundTo", [(Unit, "SetTenantContextCoreAsync", 1), (Unit, "ValidateOperationReadScope", 1)]);
        Exact(Invocation, "TryBindRead", [(Witness, "TryBindTo", 1)]);
        Exact(Invocation, "IsReadBoundTo", [(Witness, "IsBoundTo", 1)]);
        Exact(Frame, "Close", [(Coordinator, "ExecuteAsync", 1)]);
        Require(Invocation, "get_IsActive", Invocation + "/MintingSource", "MatchesCurrent");
        Require(Invocation, "get_IsActive", Candidate, "get_IsActive");
        Require(Invocation, "get_IsActive", Permissions + "TrustedOperationDescriptor", "get_IsActive");
        Require(Invocation, "get_IsActive", "System.Threading.CancellationToken", "get_IsCancellationRequested");
        Require(Invocation, "TryBindRead", Invocation, "get_IsActive");
        Require(Invocation, "IsReadBoundTo", Invocation, "get_IsActive");
        Require(Witness, "get_IsActive", Invocation, "get_IsActive");
        Require(Witness, "get_IsActive", Candidate, "get_IsActive");
        Require(Decision, "get_IsActive", Invocation, "get_IsActive");
        Require(Witness, "TryBindTo", Invocation, "TryBindRead");
        Require(Witness, "IsBoundTo", Invocation, "IsReadBoundTo");
        Require(Coordinator, "Dispose", "System.Threading.Interlocked", "Exchange");
        Require(Coordinator, "IsCurrent", Frame, "get_Closed");
        Require(Coordinator, "ExecuteAsync", Frame, "Close");
        Require(Frame, "get_Closed", "System.Threading.Volatile", "Read");
        Require(Frame, "Close", "System.Threading.Interlocked", "Exchange");
        Require(Evaluator, "AuthorizeAsync", "LearnStack.Modules.Identity.Application.Contracts.Authorization.IIdentityAuthorizationReader", "ReadAsync");
        Require(RoleEvaluator, "RecheckAsync", "LearnStack.Modules.Identity.Application.Contracts.Authorization.IIdentityAuthorizationReader", "ReadAsync");
        foreach (var (type, method, target, name) in MemberChecks.Concat(TenancyChecks)) Require(type, method, target, name);
        foreach (var property in new[] { "get_Unit", "get_Transaction", "get_Generation" })
            Require(Invocation, "IsReadBoundTo", Invocation + "/ReadBinding", property);

        var binding = census.Body(Invocation, "TryBindRead");
        var stores = census.Methods.SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
            .Where(item => item.Instruction.Operand is FieldReference field && field.DeclaringType.FullName == Invocation && field.Name == "_readBinding")
            .ToArray();
        if (stores.Count(item => item.Instruction.OpCode == OpCodes.Stfld && item.Method == binding) != 1
            || stores.Any(item => item.Instruction.OpCode == OpCodes.Stfld && item.Method != binding)
            || !binding.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Ldfld
                && instruction.Operand is FieldReference field && field.Name == "_readBinding")
            || !Calls(binding).Any(call => call.DeclaringType.FullName == "System.Threading.Monitor" && call.Name == "Enter")
            || !Calls(binding).Any(call => call.DeclaringType.FullName == "System.Threading.Monitor" && call.Name == "Exit"))
            errors.Add("One-time read consumption is checked and stored only under its private invocation monitor");
        var frame = census.Type(Frame);
        var closedField = frame.Fields.SingleOrDefault(field => field.Name == "_closed");
        var close = census.Body(Frame, "Close");
        var read = census.Body(Frame, "get_Closed");
        var closeCalls = close.Body.Instructions.Where(instruction => IsCall(instruction, "System.Threading.Interlocked", "Exchange")).ToArray();
        var readCalls = read.Body.Instructions.Where(instruction => IsCall(instruction, "System.Threading.Volatile", "Read")).ToArray();
        var cleanup = census.Body(Coordinator, "ExecuteAsync");
        var cleanupCalls = cleanup.Body.Instructions.Where(instruction => IsCall(instruction, Frame, "Close")).ToArray();
        if (closedField is not { IsPrivate: true, IsStatic: false, FieldType.FullName: "System.Int32" }
            || frame.Properties.Single(property => property.Name == "Closed").SetMethod is not null
            || closeCalls.Length != 1 || closeCalls[0].Previous.OpCode != OpCodes.Ldc_I4_1
            || !AddressesClosed(closeCalls[0].Previous.Previous)
            || readCalls.Length != 1 || !AddressesClosed(readCalls[0].Previous)
            || cleanupCalls.Length != 1 || !cleanup.Body.ExceptionHandlers.Any(handler => handler.HandlerType == ExceptionHandlerType.Finally
                && cleanup.Body.Instructions.IndexOf(handler.HandlerStart) <= cleanup.Body.Instructions.IndexOf(cleanupCalls[0])
                && (handler.HandlerEnd is null || cleanup.Body.Instructions.IndexOf(cleanupCalls[0]) < cleanup.Body.Instructions.IndexOf(handler.HandlerEnd)))
            || census.Methods.SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
                .Any(item => item.Instruction.Operand is FieldReference field && field.DeclaringType.FullName == Frame && field.Name == "_closed"
                    && !(item.Instruction.OpCode == OpCodes.Ldflda && (item.Method == close || item.Method == read))))
            errors.Add("Actual invocation cleanup publishes irreversible frame closure through its private atomic state");
        var boundTargets = new[] { (Invocation, ".ctor"), (Decision, ".ctor"), (Witness, ".ctor"),
            (Invocation + "/MintingSource", "ForInvocation"), (Decision + "/MintingSource", "ForDecision"),
            (Witness + "/MintingSource", "ForTenantRead"), (Witness, "TryBindTo"), (Witness, "IsBoundTo"),
            (Invocation, "TryBindRead"), (Invocation, "IsReadBoundTo"),
            (Permissions + "IProtectedOperationAuthorizer", "AuthorizeAsync"),
            (Permissions + "IAuthorizationCandidateSource", "ForValidatedScope"),
            (Coordinator, "ExecuteRequestAsync"), (Coordinator, "CheckHttpAsync"), (Frame, "Close") };
        if (census.Methods.SelectMany(method => method.Body.Instructions).Any(instruction => instruction.Operand is MethodReference target
            && boundTargets.Contains((target.DeclaringType.FullName, target.Name))
            && instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj)))
            errors.Add("A private operation factory or read-binding entry cannot become an executable alias");
        return errors;

        static bool AddressesClosed(Instruction instruction) => instruction.OpCode == OpCodes.Ldflda
            && instruction.Operand is FieldReference field && field.DeclaringType.FullName == Frame && field.Name == "_closed";

        void Derivers(string baseType, string[] expected)
        {
            if (!Same(census.Types.Where(type => type.BaseType?.FullName == baseType).Select(type => type.FullName), expected))
                errors.Add("The operation minter set changed: " + baseType);
        }
        void Implementations(string contract, string[] expected)
        {
            if (!Same(census.Types.Where(type => type.Interfaces.Any(item => item.InterfaceType.FullName == contract)).Select(type => type.FullName), expected))
                errors.Add("The operation authority port implementations changed: " + contract);
        }
        void Exact(string type, string name, (string Owner, string Method, int Count)[] expected)
        {
            var actual = census.Methods.SelectMany(method => Calls(method).Where(target => target.DeclaringType.FullName == type && target.Name == name)
                .Select(_ => census.Owner(method)));
            if (!Same(actual, expected.SelectMany(item => Enumerable.Repeat(item.Owner + "::" + item.Method, item.Count))))
                errors.Add("Exact operation authority callers changed: " + type + "::" + name);
        }
        void Require(string type, string method, string target, string name)
        {
            if (Calls(census.Body(type, method)).Count(call => call.DeclaringType.FullName == target && call.Name == name) != 1)
                errors.Add("Missing actual operation lifetime/reader gate: " + type + "::" + method + "->" + target + "::" + name);
        }
    }

    private static void Dirty(Census census, Func<Action> mutate)
    {
        var undo = mutate();
        try { Offenders(census).Should().NotBeEmpty("an actual authority factory/caller/binding mutation must fail"); }
        finally { undo(); }
        Offenders(census).Should().BeEmpty("the actual operation graph is restored after each control");
    }
    private static Action RemoveCall(MethodDefinition method, string type, string name) => RemoveInstruction(method.Body.Instructions.Single(instruction => IsCall(instruction, type, name)));
    private static Action RemoveInstruction(Instruction instruction) => Replace(instruction, OpCodes.Nop, null);
    private static Action Replace(Instruction instruction, OpCode opcode, object? operand)
    {
        var original = instruction.OpCode;
        var value = instruction.Operand;
        instruction.OpCode = opcode;
        instruction.Operand = operand;
        return () => { instruction.OpCode = original; instruction.Operand = value; };
    }
    private static bool IsCall(Instruction instruction, string type, string name) =>
        instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && instruction.Operand is MethodReference method
        && method.DeclaringType.FullName == type && method.Name == name;
    private static IEnumerable<MethodReference> Calls(MethodDefinition method) => method.Body.Instructions
        .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
        .Select(instruction => instruction.Operand).OfType<MethodReference>();
    private static bool Same(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
}
