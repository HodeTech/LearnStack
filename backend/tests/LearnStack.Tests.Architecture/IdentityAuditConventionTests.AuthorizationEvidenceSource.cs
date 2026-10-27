using System.Text.RegularExpressions;
using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using EvidenceCensus = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class IdentityAuditConventionTests
{
    private const string EvidenceSource = "LearnStack.Infrastructure.Persistence.AuthorizationEvidenceSource";
    private const string EvidenceSession = "LearnStack.Infrastructure.Persistence.AuthorizationEvidenceSession";
    private const string EvidenceFactoryPort = "LearnStack.SharedKernel.Audit.IAuthorizationEvidenceSessionFactory";
    private const string EvidenceSessionPort = "LearnStack.SharedKernel.Audit.IAuthorizationEvidenceSession";
    private const string EvidenceStore = "LearnStack.Infrastructure.Audit.PostgresAuditStore";
    private const string EvidenceRoleGuard = "LearnStack.Infrastructure.Persistence.ApplicationDataSource";
    private const string DeferredEvidence = "LearnStack.Api.Authentication.LiveAuthorizationRegistration/DeferredEvidenceSessions";

    [Fact]
    public void Live_Authorization_Uses_Bounded_Independent_Owners()
    {
        using var census = EvidenceCensus.Read();
        EvidenceLaneOffenders(census).Should().BeEmpty("the evidence lane is a confined standalone-audit owner, never a caller SQL executor");
        foreach (var contract in new[] { EvidenceFactoryPort, EvidenceSessionPort }) EvidenceDirty(census, EvidenceLaneOffenders, () =>
        {
            var module = census.Type(EvidenceSource).Module;
            var extra = new TypeDefinition("EvidenceControl", "AdditionalEvidenceOwner", TypeAttributes.NotPublic | TypeAttributes.Sealed,
                module.TypeSystem.Object);
            extra.Interfaces.Add(new InterfaceImplementation(module.ImportReference(census.Type(contract))));
            module.Types.Add(extra);
            return () => module.Types.Remove(extra);
        });
        foreach (var (type, method) in new[] { (EvidenceFactoryPort, "OpenAsync"), (EvidenceSource, "OpenAsync"), (EvidenceSessionPort, "BeginVerificationAsync"),
                     (EvidenceSessionPort, "CompleteAsync"), (EvidenceSessionPort, "get_Unit"), (EvidenceSessionPort, "MarkCleanupFailed") })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn }) EvidenceDirty(census, EvidenceLaneOffenders, () =>
                EvidenceExtraCall(census, census.Method(type, method), opcode));
        foreach (var (type, method, target, name) in new[]
        {
            (EvidenceSession, "OpenAsync", EvidenceRoleGuard, "RefuseBypassRoleAsync"),
            (EvidenceSession, "OpenAsync", "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources", "HoldConnection"),
            (EvidenceSession, ".ctor", "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources", "HoldActualTask"),
            (EvidenceSession, "BeginVerificationAsync", EvidenceSession, "DisposeTransactionAsync"),
            (EvidenceSession, "CleanupAsync", EvidenceSession, "DisposeTransactionAsync"),
            (EvidenceSession, "CleanupAsync", EvidenceSource, "Release"),
            (EvidenceSession, "CleanupAsync", "System.Threading.Tasks.TaskCompletionSource", "TrySetResult"),
            (EvidenceSession, "MarkCleanupFailed", "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources", "MarkCleanupFailed"),
            (EvidenceSource, "DisposeCoreAsync", "System.Threading.Tasks.Task", "WhenAll"),
            (DeferredEvidence, "OpenAsync", EvidenceSource, "OpenAsync"),
        }) EvidenceDirty(census, EvidenceLaneOffenders, () => EvidenceRemoveCall(census.Body(type, method), target, name));
        var verification = census.Body(EvidenceSession, "BeginVerificationAsync");
        var dispose = EvidenceCalls(verification, EvidenceSession, "DisposeTransactionAsync").Single();
        var begin = EvidenceCalls(verification, "Npgsql.NpgsqlConnection", "BeginTransactionAsync").Single();
        EvidenceDirty(census, EvidenceLaneOffenders, () => EvidenceReplace(
            verification.Body.Instructions.SkipWhile(instruction => instruction != dispose).TakeWhile(instruction => instruction != begin)
                .First(EvidenceAwaitAck), OpCodes.Nop, null));
        var announcement = census.Body(EvidenceSession, "AnnounceAsync").Body.Instructions.Single(instruction =>
            instruction.Operand is string text && text.StartsWith("SELECT set_config", StringComparison.Ordinal));
        EvidenceDirty(census, EvidenceLaneOffenders, () => EvidenceReplace(announcement, OpCodes.Ldstr,
            (string)announcement.Operand + ", set_config('app.scope', 'tenant', true)"));
        EvidenceDirty(census, EvidenceLaneOffenders, () =>
        {
            var source = census.Type(EvidenceSource);
            var field = source.Fields.Single(item => item.Name == "_source");
            var method = new MethodDefinition("BoxedSource", MethodAttributes.Public, source.Module.TypeSystem.Object);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, field));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            source.Methods.Add(method);
            return () => source.Methods.Remove(method);
        });
        EvidenceDirty(census, EvidenceLaneOffenders, () =>
        {
            var source = census.Type(EvidenceSource);
            var method = new MethodDefinition("RunSqlCallback", MethodAttributes.Public, source.Module.TypeSystem.Void);
            method.Parameters.Add(new ParameterDefinition(source.Module.ImportReference(typeof(Action))));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            source.Methods.Add(method);
            return () => source.Methods.Remove(method);
        });
        var constructor = census.Type(EvidenceSource).Methods.Single(method => method.IsConstructor && method.IsPublic);
        var capacity = constructor.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldc_I4_2);
        EvidenceDirty(census, EvidenceLaneOffenders, () => EvidenceReplace(capacity, OpCodes.Ldc_I4_3, null));
        EvidenceDirty(census, EvidenceLaneOffenders, () =>
        {
            var holder = census.Type(DeferredEvidence).Fields.Single();
            holder.IsInitOnly = false;
            return () => holder.IsInitOnly = true;
        });
    }

    private static List<string> EvidenceLaneOffenders(EvidenceCensus census)
    {
        var errors = new List<string>();
        EvidenceImplementers(census, errors, EvidenceFactoryPort, [EvidenceSource, DeferredEvidence]);
        EvidenceImplementers(census, errors, EvidenceSessionPort, [EvidenceSession]);
        EvidenceCallers(census, errors, EvidenceFactoryPort, "OpenAsync", [(EvidenceStore, "WriteAuthorizationStandaloneAsync", 1)]);
        EvidenceCallers(census, errors, EvidenceSource, "OpenAsync", [(DeferredEvidence, "OpenAsync", 1)]);
        EvidenceCallers(census, errors, EvidenceSessionPort, "BeginVerificationAsync", [(EvidenceStore, "WriteAuthorizationStandaloneAsync", 1)]);
        EvidenceCallers(census, errors, EvidenceSessionPort, "CompleteAsync", [(EvidenceStore, "WriteAuthorizationStandaloneAsync", 2)]);
        EvidenceCallers(census, errors, EvidenceSessionPort, "get_Unit", [(EvidenceStore, "InsertAuthorizationEvidenceAsync", 2), (EvidenceStore, "HasAuthorizationEvidenceAsync", 2)]);
        EvidenceCallers(census, errors, EvidenceSessionPort, "get_DeadlineToken", [(EvidenceStore, "InsertAuthorizationEvidenceAsync", 1), (EvidenceStore, "HasAuthorizationEvidenceAsync", 1)]);
        EvidenceCallers(census, errors, EvidenceSessionPort, "MarkCleanupFailed", [(EvidenceStore, "DisposeAuthorizationStatementAsync", 1)]);
        EvidenceCallers(census, errors, EvidenceSource, "Release", [(EvidenceSession, "CleanupAsync", 1)]);
        EvidenceCallers(census, errors, EvidenceSession, ".ctor", [(EvidenceSource, "RetainOwner", 1)]);
        EvidenceRequire(census, errors, EvidenceSession, "OpenAsync", EvidenceRoleGuard, "RefuseBypassRoleAsync");
        EvidenceRequire(census, errors, EvidenceSession, "OpenAsync", "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources", "HoldConnection");
        EvidenceRequire(census, errors, EvidenceSession, ".ctor", "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources", "HoldActualTask");
        EvidenceRequire(census, errors, EvidenceSession, "BeginVerificationAsync", EvidenceSession, "DisposeTransactionAsync");
        EvidenceRequire(census, errors, EvidenceSession, "CleanupAsync", EvidenceSession, "DisposeTransactionAsync");
        EvidenceRequire(census, errors, EvidenceSession, "CleanupAsync", "System.Threading.Tasks.TaskCompletionSource", "TrySetResult");
        EvidenceRequire(census, errors, EvidenceSession, "MarkCleanupFailed", "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources", "MarkCleanupFailed");
        EvidenceRequire(census, errors, EvidenceSource, "DisposeCoreAsync", "System.Threading.Tasks.Task", "WhenAll");

        foreach (var name in new[] { EvidenceSource, EvidenceSession })
        {
            var type = census.Type(name);
            if (!type.IsSealed || type.Fields.Any(field => field.FieldType.FullName == "Npgsql.NpgsqlDataSource" && (!field.IsPrivate || !field.IsInitOnly)))
                errors.Add("The evidence physical source and owner retain private readonly datasources: " + name);
            foreach (var method in type.Methods.Where(method => method.IsPublic || method.IsAssembly))
                if (!method.IsConstructor && EvidenceRaw(method.ReturnType)
                    || method.Parameters.Any(parameter => EvidenceRaw(parameter.ParameterType) && !method.IsConstructor)
                    || method.Parameters.Any(parameter => EvidenceCallback(parameter.ParameterType)))
                    errors.Add("The evidence lane exposes no raw source or callback: " + method.FullName);
            if (Il.ReferencedTypeNames(type).Intersect(["System.IServiceProvider", "MediatR.ISender", "MediatR.IMediator",
                "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory", "LearnStack.Modules.Identity.Domain.User",
                "LearnStack.Modules.Identity.Domain.MembershipProfile"], StringComparer.Ordinal).Any())
                errors.Add("The evidence owner cannot dispatch business work or acquire a service locator: " + name);
        }
        foreach (var (name, readers) in new[] { (EvidenceSource, new[] { "RetainOwner", "DisposeCoreAsync" }), (EvidenceSession, new[] { "OpenAsync" }) })
        {
            var field = census.Type(name).Fields.Single(item => item.FieldType.FullName == "Npgsql.NpgsqlDataSource");
            if (census.Methods.SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
                .Any(item => item.Instruction.Operand is FieldReference reference && reference.FullName == field.FullName
                    && item.Instruction.OpCode != OpCodes.Stfld
                    && (item.Instruction.OpCode != OpCodes.Ldfld || !readers.Contains(census.Owner(item.Method).Split("::", StringSplitOptions.None)[^1], StringComparer.Ordinal))))
                errors.Add("Only exact retained lifecycle members read the evidence datasource: " + name);
        }
        var constructor = census.Type(EvidenceSource).Methods.Where(method => method.IsConstructor && method.IsPublic).ToArray();
        if (constructor.Length != 1 || constructor[0].Parameters.Count != 1 || constructor[0].Parameters[0].ParameterType.FullName != "System.String"
            || !constructor[0].Body.Instructions.Any(instruction => instruction.Operand is MethodReference target && target.DeclaringType.FullName == EvidenceSource
                && target.Name == ".ctor" && instruction.Previous.OpCode == OpCodes.Ldnull && instruction.Previous.Previous.OpCode == OpCodes.Ldc_I4_2))
            errors.Add("Deployment callers cannot widen the two retained evidence owners");
        var open = census.Body(EvidenceSession, "OpenAsync");
        var guard = EvidenceCalls(open, EvidenceRoleGuard, "RefuseBypassRoleAsync").ToArray();
        var firstBegin = EvidenceCalls(open, "Npgsql.NpgsqlConnection", "BeginTransactionAsync").ToArray();
        if (guard.Length != 1 || firstBegin.Length != 1 || !EvidenceAwaited(open, guard[0], firstBegin[0])
            || !EvidenceIsCall(guard[0].Previous, EvidenceSession, "get_DeadlineToken"))
            errors.Add("Every actual evidence checkout awaits the shared role guard with its deadline before BEGIN");
        var verify = census.Body(EvidenceSession, "BeginVerificationAsync");
        var disposal = EvidenceCalls(verify, EvidenceSession, "DisposeTransactionAsync").ToArray();
        var begin = EvidenceCalls(verify, "Npgsql.NpgsqlConnection", "BeginTransactionAsync").ToArray();
        var mode = verify.Body.Instructions.Where(instruction => instruction.Operand as string == "SET TRANSACTION READ ONLY").ToArray();
        var setup = EvidenceCalls(verify, EvidenceSession, "ExecuteSetupAsync").ToArray();
        var announce = EvidenceCalls(verify, EvidenceSession, "AnnounceAsync").ToArray();
        if (disposal.Length != 1 || begin.Length != 1 || mode.Length != 1 || setup.Length != 1 || announce.Length != 1
            || !EvidenceAwaited(verify, disposal[0], begin[0]) || !EvidenceAwaited(verify, begin[0], mode[0])
            || !EvidenceAwaited(verify, setup[0], announce[0]))
            errors.Add("Duplicate verification awaits old disposal, fresh BEGIN and real READ ONLY before a fresh announcement");
        var announcement = census.Body(EvidenceSession, "AnnounceAsync");
        var sql = announcement.Body.Instructions.Select(instruction => instruction.Operand).OfType<string>()
            .SingleOrDefault(text => text.StartsWith("SELECT set_config", StringComparison.Ordinal));
        var settings = sql is null ? [] : Regex.Matches(sql, @"set_config\s*\(\s*'([^']+)'\s*,\s*([^,]+),\s*true\s*\)")
            .Select(match => match.Groups[1].Value + ":" + match.Groups[2].Value.Trim()).ToArray();
        if (!EvidenceSame(settings, ["app.tenant_id:@tenant", "app.organization_id:@organization", "app.scope:''",
            "app.resolving_host:''", "app.identity_authority_id:''", "app.identity_subject_key:''"]))
            errors.Add("The evidence announcement uses only its captured tenant/org and exact empty authority resets");
        var publish = announcement.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference target && target.DeclaringType.FullName == "LearnStack.SharedKernel.Persistence.TenantContextAnnouncement");
        var statement = EvidenceCalls(announcement, EvidenceSession, "ExecuteSetupAsync").ToArray();
        if (publish is null || statement.Length != 1 || !EvidenceAwaited(announcement, statement[0], publish))
            errors.Add("Only the actual acknowledged evidence announcement publishes generation-bound scope");
        var deferred = census.Type(DeferredEvidence);
        var forward = census.Body(DeferredEvidence, "OpenAsync");
        var holderType = "System.Func`1<" + EvidenceSource + ">";
        if (!deferred.IsNestedPrivate || !deferred.IsSealed || deferred.BaseType.FullName != "System.Object"
            || deferred.Fields.Count != 1 || deferred.Fields[0] is not { IsPrivate: true, IsInitOnly: true, IsStatic: false }
            || deferred.Fields[0].FieldType.FullName != holderType || deferred.HasProperties || deferred.HasEvents || deferred.HasNestedTypes
            || deferred.HasGenericParameters || deferred.Methods.Count != 2
            || deferred.Methods.SingleOrDefault(method => method.IsConstructor) is not { IsAssembly: true, Parameters.Count: 1 } factory
            || factory.Parameters[0].ParameterType.FullName != holderType
            || !forward.Body.Instructions.Select(instruction => instruction.OpCode.Code)
                .SequenceEqual([Code.Ldarg_0, Code.Ldfld, Code.Callvirt, Code.Ldarg_1, Code.Ldarg_2, Code.Callvirt, Code.Ret])
            || forward.Body.Instructions[1].Operand is not FieldReference held || held.FullName != deferred.Fields[0].FullName
            || !EvidenceIsCall(forward.Body.Instructions[2], holderType, "Invoke")
            || !EvidenceIsCall(forward.Body.Instructions[5], EvidenceSource, "OpenAsync"))
            errors.Add("The private deferred factory retains one immutable typed source accessor and forwards only the actual occurrence and cancellation token");
        var constructions = census.Methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == DeferredEvidence && target.Name == ".ctor").Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        if (constructions.Length != 1 || constructions[0].Instruction.OpCode != OpCodes.Newobj
            || constructions[0].Method.DeclaringType.FullName != "LearnStack.Api.Authentication.LiveAuthorizationRegistration/<>c"
            || !constructions[0].Method.Name.StartsWith("<AddLearnStackLiveAuthorization>b__", StringComparison.Ordinal))
            errors.Add("Only the actual API registration may construct its private deferred evidence accessor");
        return errors;
    }

    private static bool EvidenceRaw(TypeReference type) => Il.ExpandedTypeReferences(type).Any(item => item.FullName is
        "Npgsql.NpgsqlDataSource" or "Npgsql.NpgsqlConnection" or "Npgsql.NpgsqlTransaction" or "System.Data.Common.DbConnection" or "System.Data.Common.DbTransaction");
    private static bool EvidenceCallback(TypeReference type) => Il.ExpandedTypeReferences(type).Any(item => item.FullName.StartsWith("System.Action", StringComparison.Ordinal)
        || item.FullName.StartsWith("System.Func", StringComparison.Ordinal));
    private static bool EvidenceIsCall(Instruction instruction, string type, string name) => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj
        && instruction.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == name;
    private static IEnumerable<Instruction> EvidenceCalls(MethodDefinition method, string type, string name) => method.Body.Instructions.Where(instruction => EvidenceIsCall(instruction, type, name));
    private static bool EvidenceAwaitAck(Instruction instruction) => instruction.Operand is MethodReference target
        && target.Name == "GetResult" && target.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal);
    private static bool EvidenceAwaited(MethodDefinition method, Instruction call, Instruction next) => method.Body.Instructions
        .Skip(method.Body.Instructions.IndexOf(call) + 1).TakeWhile(instruction => instruction != next).Any(EvidenceAwaitAck)
        && method.Body.Instructions.IndexOf(call) < method.Body.Instructions.IndexOf(next);
    private static bool EvidenceSame(IEnumerable<string> actual, IEnumerable<string> expected) => actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static void EvidenceImplementers(EvidenceCensus census, List<string> errors, string contract, string[] expected)
    {
        if (!EvidenceSame(census.Types.Where(type => !type.IsInterface && type.Interfaces.Any(item => item.InterfaceType.FullName == contract)).Select(type => type.FullName), expected))
            errors.Add("Closed authorization evidence implementations changed: " + contract);
    }
    private static void EvidenceRequire(EvidenceCensus census, List<string> errors, string type, string method, string target, string name)
    {
        if (EvidenceCalls(census.Body(type, method), target, name).Count() != 1) errors.Add("Missing actual evidence lifetime gate: " + type + "::" + method + "->" + target + "::" + name);
    }
    private static void EvidenceCallers(EvidenceCensus census, List<string> errors, string type, string name, (string Owner, string Method, int Count)[] expected)
    {
        var sites = census.Methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == type && target.Name == name).Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        if (!EvidenceSame(sites.Select(item => census.Owner(item.Method)), expected.SelectMany(item => Enumerable.Repeat(item.Owner + "::" + item.Method, item.Count)))
            || sites.Any(item => item.Instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj)))
            errors.Add("Closed evidence borrower/alias set changed: " + type + "::" + name);
    }
    private static void EvidenceDirty(EvidenceCensus census, Func<EvidenceCensus, List<string>> detector, Func<Action> mutation)
    {
        var undo = mutation();
        try { detector(census).Should().NotBeEmpty("an actual evidence boundary mutation must be detected"); }
        finally { undo(); }
        detector(census).Should().BeEmpty("restoring the actual evidence boundary restores the clean control");
    }
    private static Action EvidenceReplace(Instruction instruction, OpCode opcode, object? value)
    {
        var original = instruction.OpCode;
        var operand = instruction.Operand;
        instruction.OpCode = opcode;
        instruction.Operand = value;
        return () => { instruction.OpCode = original; instruction.Operand = operand; };
    }
    private static Action EvidenceRemoveCall(MethodDefinition method, string type, string name) => EvidenceReplace(EvidenceCalls(method, type, name).Single(), OpCodes.Nop, null);
    private static Action EvidenceExtraCall(EvidenceCensus census, MethodDefinition target, OpCode opcode)
    {
        var type = census.Type(EvidenceSource);
        var method = new MethodDefinition("UnownedEvidenceCall", MethodAttributes.Private | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(opcode, type.Module.ImportReference(target)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return () => type.Methods.Remove(method);
    }
}
