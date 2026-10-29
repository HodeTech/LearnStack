using FluentAssertions;
using LearnStack.Api.Idempotency;
using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Permissions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0079: a receipt identifies the original target and authorizes a new read, never a mutation.</summary>
public sealed class IdempotencyConventionTests
{
    private const string Dispatcher = "LearnStack.Api.Idempotency.ProtectedReceiptReplayDispatcher";
    private const string HttpAdmission = "LearnStack.Api.Idempotency.ProtectedReplayHttpAdmission";
    private const string Admission = HttpAdmission + "/Admission";
    private const string Binding = HttpAdmission + "/RequestBinding";
    private const string Authorize = HttpAdmission + "::AuthorizeAsync";
    private const string Consume = HttpAdmission + "::Consume";
    private const string AdmissionFile = "LearnStack.Api/Idempotency/ProtectedReplayHttpAdmission.cs";
    private const string FilterFile = "LearnStack.Api/Idempotency/ProtectedIdempotencyFilter.cs";
    private static readonly Type[] Receipts =
    [
        typeof(ReadMembershipReceiptQuery), typeof(ReadMemberProfileReceiptQuery), typeof(ReadOwnMembershipProfileReceiptQuery),
        typeof(ReadRoleReceiptQuery), typeof(ReadOrganizationReceiptQuery), typeof(ReadDefaultOrganizationReceiptQuery),
    ];

    [Fact]
    public void Protected_Idempotent_Replay_Reauthorizes_Original_Target()
    {
        var catalog = new OperationPolicyCatalog([
            new TenancyOperationPolicySource(), new CustomizationOperationPolicySource(), new EducationOperationPolicySource(),
            new IdentityOperationPolicySource(), new BackgroundJobOperationPolicySource()]);
        foreach (var receipt in Receipts)
        {
            catalog.TryGet(receipt, out var policy).Should().BeTrue("Fix: classify every receipt read explicitly");
            policy.Kind.Should().Be(receipt == typeof(ReadOwnMembershipProfileReceiptQuery)
                ? OperationPolicyKind.OwnMembershipRead : OperationPolicyKind.Protected);
            policy.RequiresOrglessWrite.Should().BeFalse("disclosure is not the original mutation's eligibility");
            policy.RequiresOrganization.Should().BeFalse();
            policy.ReadScope.Should().Be(OperationReadScope.Scoped);
            if (policy.Kind == OperationPolicyKind.Protected)
                policy.Alternatives.Should().NotBeEmpty().And.OnlyContain(clause => clause.ReadScope == OperationReadScope.Scoped);
            else policy.Alternatives.Should().BeEmpty("own receipt disclosure uses coverage, not an invented permission clause");
        }
        WithProduction(modules =>
        {
            MarkerOffenders(modules).Should().BeEmpty("Fix: keep the receipt marker on exactly the six sealed original-target reads");
            DispatcherOffenders(modules).Should().BeEmpty("Fix: replay must construct the six fresh receipt queries, never the original mutation");
            AdmissionOffenders(modules).Should().BeEmpty("Fix: only the bound HTTP admission may inspect completed outcomes or construct its private one-use binding");
        });
        var mapping = typeof(ProtectedAdministrationOperation).Assembly.GetType(HttpAdmission, throwOnError: true)!
            .GetMethod("ReceiptRequest", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        ReceiptPolicyMapping().Keys.Should().BeEquivalentTo(Enum.GetValues<ProtectedAdministrationOperation>());
        foreach (var (operation, receipt) in ReceiptPolicyMapping())
            mapping.Invoke(null, [operation]).Should().Be(receipt, "each mutation must select its exact current disclosure policy");
        foreach (var unknown in new[] { 0, -1, int.MaxValue })
        {
            Action invoke = () => mapping.Invoke(null, [(ProtectedAdministrationOperation)unknown]);
            invoke.Should().Throw<System.Reflection.TargetInvocationException>()
                .Which.InnerException.Should().BeOfType<ArgumentOutOfRangeException>();
        }
        AdmissionGuardOffenders(ReadAdmissionSources()).Should().BeEmpty("Fix: publish and consume admission only while its actual request and source binding remain current");
    }

    [Fact]
    public void Receipt_census_detects_missing_indirect_extra_and_mutation_targets()
    {
        WithProduction(modules =>
        {
            var names = ReceiptNames();
            names.Add(Dispatcher);
            names.Add(typeof(CreateManagedOrganizationCommand).FullName!);
            var types = Types(modules).Where(type => names.Contains(type.FullName))
                .ToDictionary(type => type.FullName, StringComparer.Ordinal);
            var receipt = types[typeof(ReadOwnMembershipProfileReceiptQuery).FullName!];
            var marker = receipt.Interfaces.Single(item => item.InterfaceType.FullName == typeof(IIdempotencyReceiptRead).FullName);
            receipt.Interfaces.Remove(marker);
            try { MarkerOffenders(modules).Should().Contain(receipt.FullName); }
            finally { receipt.Interfaces.Add(marker); }
            var sealedState = receipt.IsSealed;
            receipt.IsSealed = false;
            try { MarkerOffenders(modules).Should().Contain(receipt.FullName); }
            finally { receipt.IsSealed = sealedState; }

            var module = receipt.Module;
            var alias = new TypeDefinition("ReceiptControl", "Alias", TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.NotPublic);
            alias.Interfaces.Add(new InterfaceImplementation(marker.InterfaceType));
            var extra = new TypeDefinition("ReceiptControl", "Extra", TypeAttributes.NotPublic | TypeAttributes.Sealed, module.TypeSystem.Object);
            extra.Interfaces.Add(new InterfaceImplementation(alias));
            module.Types.Add(alias);
            module.Types.Add(extra);
            try { MarkerOffenders(modules).Should().Contain(extra.FullName, "indirect marker aliases are not a new authority source"); }
            finally { module.Types.Remove(extra); module.Types.Remove(alias); }

            var dispatcher = types[Dispatcher].Methods.Single(method => method.Name == "ReadAsync");
            var construction = dispatcher.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Newobj
                && instruction.Operand is MethodReference target && ReceiptNames().Contains(target.DeclaringType.FullName));
            var index = dispatcher.Body.Instructions.IndexOf(construction);
            dispatcher.Body.Instructions.Remove(construction);
            try { DispatcherOffenders(modules).Should().NotBeEmpty("removing one actual receipt target must be detected"); }
            finally { dispatcher.Body.Instructions.Insert(index, construction); }
            var mutation = types[typeof(CreateManagedOrganizationCommand).FullName!].Methods
                .Single(method => method.IsConstructor && method.IsPublic);
            var extraMutation = Instruction.Create(OpCodes.Newobj, dispatcher.Module.ImportReference(mutation));
            dispatcher.Body.Instructions.Add(extraMutation);
            try { DispatcherOffenders(modules).Should().Contain(mutation.DeclaringType.FullName, "an original mutation is not a replay guard"); }
            finally { dispatcher.Body.Instructions.Remove(extraMutation); }
            MarkerOffenders(modules).Should().BeEmpty();
            DispatcherOffenders(modules).Should().BeEmpty();
            AdmissionDirtyControls(modules);
        });
        var sources = ReadAdmissionSources();
        foreach (var (file, relation) in AdmissionGuardRelations())
        {
            var compact = Compact(sources[file]);
            var needle = Compact(relation);
            compact.Should().Contain(needle, "every dirty control must change an actual production guard relation");
            var dirty = new Dictionary<string, string>(sources, StringComparer.Ordinal)
            {
                [file] = compact.Replace(needle, "true", StringComparison.Ordinal) + "\n// " + needle,
            };
            AdmissionGuardOffenders(dirty).Should().Contain(file + ": " + needle,
                "removing a guard and leaving its spelling in a comment must not preserve admission");
        }
        AdmissionGuardOffenders(sources).Should().BeEmpty();
    }

    private static string[] MarkerOffenders(ModuleDefinition[] modules)
    {
        var all = Types(modules).ToArray();
        var types = all.ToDictionary(type => Symbol(type));
        var expected = ReceiptNames();
        var actual = all.Where(type => !type.IsInterface && HasMarker(type, types, [])).ToArray();
        var actualNames = actual.Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
        return expected.Except(actualNames, StringComparer.Ordinal)
            .Concat(actualNames.Except(expected, StringComparer.Ordinal))
            .Concat(actual.GroupBy(type => type.FullName, StringComparer.Ordinal).Where(group => group.Count() != 1).Select(group => group.Key))
            .Concat(actual.Where(type => !type.IsSealed || type.IsAbstract
                || type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "LearnStack.SharedKernel.Tenancy.PublicSurfaceAttribute")
                || type.Properties.Count(property => property.GetMethod?.IsPublic == true) != 1)
                .Select(type => type.FullName))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HasMarker(TypeDefinition type, Dictionary<(string Assembly, string Name), TypeDefinition> types,
        HashSet<TypeDefinition> visited)
    {
        if (!visited.Add(type)) return false;
        foreach (var contract in type.Interfaces)
        {
            if (contract.InterfaceType.FullName == typeof(IIdempotencyReceiptRead).FullName
                && Symbol(contract.InterfaceType).Assembly == typeof(IIdempotencyReceiptRead).Assembly.GetName().Name) return true;
            if (types.TryGetValue(Symbol(contract.InterfaceType), out var parent) && HasMarker(parent, types, visited)) return true;
        }
        return type.BaseType is { } baseType && types.TryGetValue(Symbol(baseType), out var ancestor)
            && HasMarker(ancestor, types, visited);
    }

    private static (string Assembly, string Name) Symbol(TypeReference reference)
    {
        var type = reference.GetElementType();
        return (type.Scope is AssemblyNameReference assembly ? assembly.Name : type.Module.Assembly.Name.Name, type.FullName);
    }

    private static string[] DispatcherOffenders(ModuleDefinition[] modules)
    {
        var owner = Types(modules).Single(type => type.FullName == Dispatcher);
        var method = owner.Methods.Single(candidate => candidate.Name == "ReadAsync");
        var constructions = method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Newobj)
            .Select(instruction => (MethodReference)instruction.Operand)
            .Where(target => target.DeclaringType.Namespace.Contains(".Application.Contracts", StringComparison.Ordinal))
            .Select(target => target.DeclaringType.FullName).ToArray();
        var expected = ReceiptNames();
        return expected.Except(constructions, StringComparer.Ordinal)
            .Concat(constructions.Except(expected, StringComparer.Ordinal))
            .Concat(constructions.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() != 1).Select(group => group.Key))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<ProtectedAdministrationOperation, Type> ReceiptPolicyMapping() => new()
    {
        [ProtectedAdministrationOperation.SetMembershipProfile] = typeof(ReadMemberProfileReceiptQuery),
        [ProtectedAdministrationOperation.SetOwnMembershipProfile] = typeof(ReadOwnMembershipProfileReceiptQuery),
        [ProtectedAdministrationOperation.ChangeMembershipStatus] = typeof(ReadMembershipReceiptQuery),
        [ProtectedAdministrationOperation.ReplaceMembershipRoleAssignments] = typeof(ReadMembershipReceiptQuery),
        [ProtectedAdministrationOperation.ChangeRoleGrants] = typeof(ReadRoleReceiptQuery),
        [ProtectedAdministrationOperation.AdoptRoleGrantManifest] = typeof(ReadRoleReceiptQuery),
        [ProtectedAdministrationOperation.CreateOrganization] = typeof(ReadOrganizationReceiptQuery),
        [ProtectedAdministrationOperation.RenameOrganization] = typeof(ReadOrganizationReceiptQuery),
        [ProtectedAdministrationOperation.ArchiveOrganization] = typeof(ReadOrganizationReceiptQuery),
        [ProtectedAdministrationOperation.SelectDefaultOrganization] = typeof(ReadDefaultOrganizationReceiptQuery),
    };

    private static string[] AdmissionOffenders(ModuleDefinition[] modules)
    {
        var types = Types(modules).ToArray();
        var methods = types.SelectMany(type => type.Methods).Where(method => method.HasBody).ToArray();
        var asyncOwners = methods.SelectMany(method => method.CustomAttributes
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")
            .Select(attribute => (State: Symbol((TypeReference)attribute.ConstructorArguments[0].Value),
                Owner: method.DeclaringType.FullName + "::" + method.Name)))
            .ToDictionary(item => item.State, item => item.Owner);
        string Owner(MethodDefinition method) => asyncOwners.GetValueOrDefault(Symbol(method.DeclaringType),
            method.DeclaringType.FullName + "::" + method.Name);
        var references = methods.SelectMany(method => method.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (Method: method, Instruction: instruction, Target: (MethodReference)instruction.Operand))).ToArray();
        var failures = new List<string>();
        var store = types.Single(type => type.FullName == typeof(IIdempotencyStore).FullName);
        var storeTypes = types.Where(type => type.Interfaces.Any(item => Symbol(item.InterfaceType) == Symbol(store)))
            .Select(Symbol).Append(Symbol(store)).ToHashSet();
        var lookups = references.Where(reference => reference.Target.Name == nameof(IIdempotencyStore.ReadCompletedAsync)
            && storeTypes.Contains(Symbol(reference.Target.DeclaringType))).ToArray();
        if (lookups.Length != 1 || lookups.Any(reference => Owner(reference.Method) != Authorize
            || reference.Instruction.OpCode.Code is not (Code.Call or Code.Callvirt)
            || Symbol(reference.Target.DeclaringType) != Symbol(store))) failures.Add("completed lookup callers");

        foreach (var name in new[] { Admission, Binding })
        {
            var type = types.Single(candidate => candidate.FullName == name);
            if (!type.IsNestedPrivate || !type.IsSealed || type.IsAbstract) failures.Add(name + " visibility");
            var constructors = type.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
            var construction = references.Where(reference => reference.Target.Name == ".ctor"
                && Symbol(reference.Target.DeclaringType) == Symbol(type)).ToArray();
            if (constructors.Length != 1 || construction.Length != 1
                || construction.Any(reference => Owner(reference.Method) != Authorize
                    || reference.Instruction.OpCode != OpCodes.Newobj
                    || reference.Target.FullName != constructors[0].FullName)) failures.Add(name + " constructors");
        }
        var matches = references.Where(reference => reference.Target.Name == "Matches"
            && Symbol(reference.Target.DeclaringType) == Symbol(types.Single(type => type.FullName == Binding))).ToArray();
        if (!matches.Select(reference => Owner(reference.Method)).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { Authorize, Authorize, Consume }.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || matches.Any(reference => reference.Instruction.OpCode.Code is not (Code.Call or Code.Callvirt)))
            failures.Add("binding consumers");
        var consume = Body(types, HttpAdmission, "Consume");
        var consumed = types.Single(type => type.FullName == Admission).Fields.Single(field => field.Name == "Consumed");
        var uses = methods.SelectMany(method => method.Body.Instructions
            .Where(instruction => instruction.Operand is FieldReference field && field.FullName == consumed.FullName
                && Symbol(field.DeclaringType) == Symbol(consumed.DeclaringType))
            .Select(instruction => (Method: method, Instruction: instruction))).ToArray();
        var exchanges = consume.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Call
            && instruction.Operand is MethodReference method && method.DeclaringType.FullName == "System.Threading.Interlocked"
            && method.Name == "Exchange" && method.ReturnType.FullName == "System.Int32").ToArray();
        if (uses.Length != 1 || uses[0].Method != consume || uses[0].Instruction.OpCode != OpCodes.Ldflda
            || exchanges.Length != 1 || exchanges[0].Previous?.OpCode != OpCodes.Ldc_I4_1
            || exchanges[0].Previous?.Previous != uses[0].Instruction) failures.Add("one-use admission");
        if (references.Any(reference => Owner(reference.Method).StartsWith(HttpAdmission, StringComparison.Ordinal)
            && storeTypes.Contains(Symbol(reference.Target.DeclaringType))
            && reference.Target.Name is nameof(IIdempotencyStore.TryClaimAsync) or nameof(IIdempotencyStore.CompleteAsync)
                or nameof(IIdempotencyStore.AbandonAsync))) failures.Add("HTTP admission writes outcomes");
        var policies = Body(types, HttpAdmission, "ReceiptRequest").Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Ldtoken).Select(instruction => instruction.Operand)
            .OfType<TypeReference>().Select(Symbol).ToArray();
        var expected = Receipts.Select(type => (type.Assembly.GetName().Name!, type.FullName!)).ToArray();
        if (policies.Length != expected.Length || expected.Except(policies).Any() || policies.Except(expected).Any())
            failures.Add("closed receipt policies");
        return failures.ToArray();
    }

    private static MethodDefinition Body(TypeDefinition[] types, string owner, string name)
    {
        var method = types.Single(type => type.FullName == owner).Methods.Single(candidate => candidate.Name == name);
        var state = method.CustomAttributes.SingleOrDefault(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute");
        return state is null ? method : types.Single(type => Symbol(type) == Symbol((TypeReference)state.ConstructorArguments[0].Value))
            .Methods.Single(candidate => candidate.Name == "MoveNext");
    }

    private static void AdmissionDirtyControls(ModuleDefinition[] modules)
    {
        var types = Types(modules).ToArray();
        var authorize = Body(types, HttpAdmission, "AuthorizeAsync");
        var consume = Body(types, HttpAdmission, "Consume");
        var unrelated = Body(types, Dispatcher, "ReadAsync");
        void Dirty(Action mutate, Action restore, string expected)
        {
            mutate();
            try { AdmissionOffenders(modules).Should().Contain(expected); }
            finally { restore(); }
            AdmissionOffenders(modules).Should().BeEmpty("each dirty control restores the actual compiled authority graph");
        }
        var lookup = authorize.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == typeof(IIdempotencyStore).FullName && method.Name == nameof(IIdempotencyStore.ReadCompletedAsync));
        var lookupCode = lookup.OpCode;
        Dirty(() => lookup.OpCode = OpCodes.Ldftn, () => lookup.OpCode = lookupCode, "completed lookup callers");
        var index = authorize.Body.Instructions.IndexOf(lookup);
        Dirty(() => authorize.Body.Instructions.Remove(lookup), () => authorize.Body.Instructions.Insert(index, lookup), "completed lookup callers");
        foreach (var opcode in new[] { OpCodes.Callvirt, OpCodes.Ldftn })
        {
            var extra = Instruction.Create(opcode, (MethodReference)lookup.Operand);
            Dirty(() => unrelated.Body.Instructions.Add(extra), () => unrelated.Body.Instructions.Remove(extra), "completed lookup callers");
        }
        foreach (var name in new[] { Admission, Binding })
        {
            var type = types.Single(candidate => candidate.FullName == name);
            var attributes = type.Attributes;
            Dirty(() => type.IsSealed = false, () => type.Attributes = attributes, name + " visibility");
            Dirty(() => type.IsNestedPublic = true, () => type.Attributes = attributes, name + " visibility");
            var constructor = type.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            var extra = Instruction.Create(OpCodes.Newobj, constructor);
            Dirty(() => unrelated.Body.Instructions.Add(extra), () => unrelated.Body.Instructions.Remove(extra), name + " constructors");
        }
        var exchange = consume.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == "System.Threading.Interlocked" && method.Name == "Exchange");
        var once = exchange.Previous;
        Dirty(() => once.OpCode = OpCodes.Ldc_I4_0, () => once.OpCode = OpCodes.Ldc_I4_1, "one-use admission");
        var matches = consume.Body.Instructions.Single(instruction => instruction.Operand is MethodReference method
            && method.DeclaringType.FullName == Binding && method.Name == "Matches");
        var matchCode = matches.OpCode;
        Dirty(() => matches.OpCode = OpCodes.Ldftn, () => matches.OpCode = matchCode, "binding consumers");
        var policy = Body(types, HttpAdmission, "ReceiptRequest").Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Ldtoken);
        var original = policy.Operand;
        Dirty(() => policy.Operand = types.Single(type => type.FullName == typeof(CreateManagedOrganizationCommand).FullName),
            () => policy.Operand = original, "closed receipt policies");
        var claim = types.Single(type => type.FullName == typeof(IIdempotencyStore).FullName).Methods.Single(method => method.Name == nameof(IIdempotencyStore.TryClaimAsync));
        var write = Instruction.Create(OpCodes.Callvirt, claim);
        Dirty(() => authorize.Body.Instructions.Add(write), () => authorize.Body.Instructions.Remove(write), "HTTP admission writes outcomes");
    }

    private static Dictionary<string, string> ReadAdmissionSources() => new[] { AdmissionFile, FilterFile }
        .ToDictionary(file => file, file => File.ReadAllText(Path.Combine(SourceScan.SourceRoot, file)), StringComparer.Ordinal);

    private static string Compact(string code) => SourceText.WithoutWhitespace(SourceText.WithoutCommentsOrLiterals(code));

    private static (string File, string Relation)[] AdmissionGuardRelations() =>
    [
        (AdmissionFile, "if (!AdministrationHttpInput.SupportsJsonMedia(http.Request)) return Failure(\"lockey_validation_failed\");"),
        (AdmissionFile, "&& AdministrationHttpInput.SupportsJsonMedia(current.Request)"),
        (FilterFile, "if (!AdministrationHttpInput.SupportsJsonMedia(context.HttpContext.Request)) { context.Result = AdministrationHttpInput.Invalid(); return; }"),
        (AdmissionFile, "var requestType = frozen is null ? declared.RequestType : ReceiptRequest(descriptor.Operation);"),
        (AdmissionFile, "if (result.IsFailure) return result; if (!binding.Matches(http, context, descriptor, key, fingerprint, expected)) return Failure(\"lockey_forbidden\"); http.Features.Set(new Admission(binding, frozen));"),
        (AdmissionFile, "if (http.Features.Get<Admission>() is not { } admission) return new(OperationHttpClassifier.RequiresProtectedAdmission(http.GetEndpoint()) ? ProtectedHttpAdmissionKind.Refused : ProtectedHttpAdmissionKind.Unbound);"),
        (AdmissionFile, "if (Interlocked.Exchange(ref admission.Consumed, 1) != 0 || !admission.Binding.Matches(http, context, descriptor, key, fingerprint, expected)) return new(ProtectedHttpAdmissionKind.Refused);"),
        (AdmissionFile, "return admission.Response is { } stored ? new(ProtectedHttpAdmissionKind.Replay, stored) : new(ProtectedHttpAdmissionKind.Mutation);"),
        (AdmissionFile, "ReferenceEquals(http, current) && ReferenceEquals(endpoint, current.GetEndpoint()) && ReferenceEquals(context, scope) && !current.RequestAborted.IsCancellationRequested"),
        (AdmissionFile, "scope.IsResolved && scope.TenantId == _tenant && scope.OrganizationId == _organization && scope.UserId == _actor && scope.Origin == _origin && candidate.IsActive"),
        (AdmissionFile, "candidate.Actor == _actor && candidate.TenantId == _tenant && candidate.OrganizationId == _organization"),
        (AdmissionFile, "descriptor == operation && key == currentKey && fingerprint == currentFingerprint && expected == currentExpected"),
        (AdmissionFile, "classifier.Classify(endpoint, out var currentPolicy) == HttpOperationClassification.Application && ReferenceEquals(policy, currentPolicy)"),
        (FilterFile, "if (admission.Kind == ProtectedHttpAdmissionKind.Replay) { await ReplayAsync(context, admission.Response, cancellationToken); return; }"),
    ];

    private static string[] AdmissionGuardOffenders(Dictionary<string, string> sources) => AdmissionGuardRelations()
        .Where(relation => !Compact(sources[relation.File]).Contains(Compact(relation.Relation), StringComparison.Ordinal))
        .Select(relation => relation.File + ": " + Compact(relation.Relation)).ToArray();

    private static HashSet<string> ReceiptNames() => Receipts.Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal);
    private static IEnumerable<TypeDefinition> Types(IEnumerable<ModuleDefinition> modules) => modules.SelectMany(module => module.Types).SelectMany(Types);
    private static IEnumerable<TypeDefinition> Types(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Types));
    private static void WithProduction(Action<ModuleDefinition[]> assertion)
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try { assertion(modules); }
        finally { foreach (var module in modules) module.Dispose(); }
    }
}
