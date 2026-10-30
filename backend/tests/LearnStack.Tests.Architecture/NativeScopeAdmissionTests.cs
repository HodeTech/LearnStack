using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0078's separate native evidence and closed global-self/READ ONLY owner boundaries.</summary>
public sealed partial class NativeScopeAdmissionTests
{
    private const string Auth = "LearnStack.Api.Authentication.";
    private const string Native = Auth + "NativeScopeEvidence";
    private const string Source = Auth + "NativeSessionSourceFeature";
    private const string SourceMiddleware = Auth + "NativeSessionSourceMiddleware";
    private const string Binding = Auth + "TenantIdentityBindingAdmissionMiddleware";
    private const string Entry = Auth + "GlobalSelfEntryMiddleware";
    private const string Witness = "LearnStack.SharedKernel.Identity.OwnMembershipScopeWitness";
    private const string Minter = Witness + "/MintingSource";
    private const string BindingService = "LearnStack.Modules.Identity.Infrastructure.Bindings.GlobalIdentityBindingService";
    private const string ScopeSource = BindingService + "/ScopeSource";
    private const string Unit = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
    private const string UnitPort = "LearnStack.SharedKernel.Persistence.IPreContextSelfDiscoveryUnitOfWork";
    private const string Labels = "LearnStack.Modules.Tenancy.Infrastructure.Persistence.PreContextSelfDiscoveryScopeReader";
    private static readonly string[] ClearedAuthorityKeys = ["scope", "resolving_host", "identity_authority_id", "identity_subject_key"];
    private static readonly string[] LazyEntryPorts =
    [
        "LearnStack.Modules.Identity.Application.Contracts.Entry.IIdentitySelfStateReader",
        "LearnStack.Modules.Identity.Application.Contracts.Entry.IIdentitySelfBootstrapper",
        "LearnStack.Modules.Identity.Application.Contracts.Entry.IIdentitySelfDiscovery",
        "LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle.IOwnMembershipScopeLabels",
    ];

    /// <summary>Security 11: native selectors are a distinct immutable source bound to the exact request and real host.</summary>
    [Fact]
    public void Native_Scope_Evidence_Is_Separate_And_Host_Bound()
    {
        using var census = Census.Read();
        NativeOffenders(census).Should().BeEmpty("Fix: retain separate immutable native/provider facts, exact mint callers and current source/host checks (ADR-0078)");
        var constructor = census.Type(Native).Methods.Single(method => method.IsConstructor);
        var attributes = constructor.Attributes;
        Dirty(census, NativeOffenders, () =>
        {
            constructor.Attributes = (attributes & ~Mono.Cecil.MethodAttributes.MemberAccessMask) | Mono.Cecil.MethodAttributes.Public;
            return () => constructor.Attributes = attributes;
        });
        Dirty(census, NativeOffenders, () => RemoveCall(census.Body(Native, "IsCurrent"), Source, "IsCurrent"));
        Dirty(census, NativeOffenders, () => RemoveCall(census.Body(Source, "IsCurrent"),
            "System.Security.Cryptography.CryptographicOperations", "FixedTimeEquals"));
        Dirty(census, NativeOffenders, () => AddCall(census.Type(Native).Module,
            census.Type(Native).Methods.Single(method => method.Name == "TryMint")));
    }

    /// <summary>Security 11: only the three exact subject/quota/native-host descriptors bypass ordinary resolution.</summary>
    [Fact]
    public void Global_self_descriptors_and_pipeline_are_a_closed_early_branch()
    {
        var operation = ProductionAssemblies.All().Single(assembly => assembly.GetName().Name == "LearnStack.Api")
            .GetType(Auth + "GlobalSelfOperation", throwOnError: true)!;
        var descriptors = ((IEnumerable)operation.GetProperty("All", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
            .Cast<object>().Select(value => new Descriptor(
                Convert.ToInt32(operation.GetProperty("Kind")!.GetValue(value), System.Globalization.CultureInfo.InvariantCulture),
                (string)operation.GetProperty("Path")!.GetValue(value)!,
                (string)operation.GetProperty("Method")!.GetValue(value)!)).ToArray();
        DescriptorOffenders(descriptors).Should().BeEmpty("Fix: retain exactly GET self, POST self/bootstrap and GET self/contexts, with no wildcard or generic unresolved route");
        DescriptorOffenders([.. descriptors, new(1, "/api/v1/identity/self/foreign", "GET")]).Should().NotBeEmpty();
        DescriptorOffenders(descriptors.Select(item => item.Path.EndsWith("bootstrap", StringComparison.Ordinal)
            ? item with { Method = "GET" } : item)).Should().NotBeEmpty();

        var program = File.ReadAllText(Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.Api", "Program.cs"));
        PipelineOffenders(program).Should().BeEmpty("Fix: qualify source after subject quota and terminate the exact global-self branch before binding, context resolution and authorization");
        PipelineOffenders(program.Replace("app.UseLearnStackNativeSessionSource();", string.Empty, StringComparison.Ordinal)).Should().NotBeEmpty();
        PipelineOffenders(program.Replace("app.UseLearnStackTenantSubjectAdmission();", "app.UseMiddleware<GlobalSelfEntryMiddleware>();", StringComparison.Ordinal)).Should().NotBeEmpty();

        using var census = Census.Read();
        HttpEntryOffenders(census.Modules).Should().BeEmpty("Fix: global-self dispatch uses only its typed subject-confined entry ports; it never dispatches MediatR or publishes tenant context");
        AssertEntryClassDispatchControls(census.Modules);
        Dirty(census, EntryOffenders, () => AddCall(census.Type(Entry).Module,
            census.Type("LearnStack.SharedKernel.Tenancy.TenantContextFactory").Methods.Single(method => method.Name == "CreateForNativeScope"), Entry));
        foreach (var port in LazyEntryPorts)
            Dirty(census, EntryOffenders, () =>
            {
                var parameter = census.Type(Entry).Methods.Single(method => method.Name == "InvokeAsync").Parameters
                    .Single(item => IsLazyPort(item.ParameterType, port));
                var factory = (GenericInstanceType)parameter.ParameterType;
                parameter.ParameterType = factory.GenericArguments[0];
                return () => parameter.ParameterType = factory;
            });
    }

    /// <summary>Standard 05: one captured own scope enrolls one sequential ordinary-source physically READ ONLY owner.</summary>
    [Fact]
    public void Own_scope_discovery_has_one_confined_minter_and_read_only_label_owner()
    {
        using var census = Census.Read();
        DiscoveryOffenders(census).Should().BeEmpty("Fix: mint only subject-derived own Membership scopes and consume them once on the common READ ONLY owner; label SQL remains Tenancy-owned (ADR-0078)");
        Dirty(census, DiscoveryOffenders, () =>
        {
            var module = census.Type(ScopeSource).Module;
            var extra = new TypeDefinition("NativeControl", "ForeignScopeSource", Mono.Cecil.TypeAttributes.NotPublic,
                module.ImportReference(census.Type(Minter)));
            module.Types.Add(extra);
            return () => module.Types.Remove(extra);
        });
        Dirty(census, DiscoveryOffenders, () => RemoveCall(census.Body(Unit, "BeginSelfDiscoveryTransactionAsync"), Witness, "TryBindTo"));
        Dirty(census, DiscoveryOffenders, () =>
        {
            var instruction = Calls(census.Body(Unit, "BeginSelfDiscoveryTransactionAsync"), Unit, "BeginTransactionCoreAsync").Single();
            // The final token argument is loaded from the async state machine;
            // the preceding constants select ReadOnly and the non-global source.
            var mode = Previous(instruction, 4);
            mode.OpCode.Should().Be(OpCodes.Ldc_I4_1);
            return Replace(mode, OpCodes.Ldc_I4_0, null);
        });
        Dirty(census, DiscoveryOffenders, () =>
        {
            var sql = Strings(census.Body(Labels, "ReadAsync")).Single(item => item.Text.Contains("FROM public.tenants", StringComparison.Ordinal));
            return Replace(sql.Instruction, OpCodes.Ldstr, sql.Text.Replace("FROM public.tenants", "FROM public.membership_profiles", StringComparison.Ordinal));
        });
    }

    private static List<string> NativeOffenders(Census census)
    {
        var errors = new List<string>();
        foreach (var name in new[] { Native, Source })
        {
            var type = census.Type(name);
            if (!type.IsSealed || type.IsPublic || type.Fields.Any(field => !field.IsStatic && !field.IsInitOnly)
                || type.Properties.Any(property => property.SetMethod is not null))
                errors.Add("Native evidence must remain sealed, internal and immutable: " + name);
        }
        if (!census.Type(Native).Methods.Single(method => method.IsConstructor).IsPrivate)
            errors.Add("Native evidence construction is private to its checked factory");
        RequireCallers(census, errors, Native, ".ctor", [Native + ".TryMint"]);
        RequireCallers(census, errors, Native, "TryMint", [Binding + ".InvokeAsync"]);
        RequireCallers(census, errors, Source, ".ctor", [SourceMiddleware + ".InvokeAsync"]);
        foreach (var item in census.Bodies)
            foreach (var instruction in item.Body.Body.Instructions)
                if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                    && field.DeclaringType.FullName is Native or Source
                    && (item.Owner != field.DeclaringType.FullName || item.Name != ".ctor"))
                    errors.Add("Native evidence backing-field write outside construction: " + item.Owner + "." + item.Name);
        RequireCalls(census.Body(Native, "TryMint"), errors, (Source, "IsCurrent"),
            (Auth + "VerifiedProviderSubject", "get_Issuer"), (Auth + "VerifiedProviderSubject", "get_Subject"),
            (Auth + "VerifiedProviderSubject", "get_TenantId"), (Auth + "VerifiedProviderSubject", "get_OrganizationId"),
            (Auth + "VerifiedProviderSubject", "get_ClaimedActor"));
        RequireCalls(census.Body(Native, "IsCurrent"), errors, (Source, "IsCurrent"));
        RequireFields(census.Body(Native, "IsCurrent"), errors, "_request", "_source", "_subject", "_identity");
        RequireCalls(census.Body(Source, "IsCurrent"), errors,
            ("LearnStack.Api.Tenancy.EffectiveHostAccessor", "IsTrustedHop"),
            ("LearnStack.Api.Tenancy.EffectiveHostAccessor", "For"),
            (Auth + "NativeSessionSourcePolicy", "Matches"),
            (Auth + "NativeSessionSourcePolicy", "HasOnlyKnownNativeHeaders"),
            ("System.Security.Cryptography.CryptographicOperations", "FixedTimeEquals"),
            (Auth + "TenantSubjectAdmissionProvenance", "Matches"),
            (Auth + "SubjectAdmissionDecision", "get_IsAcquired"));
        RequireFields(census.Body(Source, "IsCurrent"), errors, "_request", "_host", "_subject", "_decision", "_provenance", "_path", "_method", "_credentialDigest");
        foreach (var name in new[] { Native, Source })
        {
            var method = census.Body(name, "ToString");
            if (!Strings(method).Select(item => item.Text).SequenceEqual([name.Split('.').Last()], StringComparer.Ordinal)
                || method.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Ldfld))
                errors.Add("Native evidence formatting may expose only its type name: " + name);
        }
        return errors;
    }

    private static List<string> EntryOffenders(Census census)
    {
        var errors = new List<string>();
        var entry = census.Type(Entry);
        var types = Il.ReferencedTypeNames(entry).ToArray();
        if (types.Any(name => name is "MediatR.ISender" or "MediatR.IMediator" or "LearnStack.SharedKernel.Tenancy.ITenantContextAccessor"
            or "LearnStack.SharedKernel.Tenancy.TenantContextFactory" or "LearnStack.SharedKernel.Tenancy.TenantContext"))
            errors.Add("Global-self branch cannot dispatch ordinary application requests or construct/publish tenant context");
        var invocation = entry.Methods.Single(method => method.Name == "InvokeAsync");
        foreach (var port in LazyEntryPorts)
            if (invocation.Parameters.Count(parameter => IsLazyPort(parameter.ParameterType, port)) != 1
                || invocation.Parameters.Any(parameter => CarriesPort(parameter.ParameterType, port) && !IsLazyPort(parameter.ParameterType, port))
                || entry.Fields.Any(field => CarriesPort(field.FieldType, port))
                || entry.Methods.Where(method => method.IsConstructor).SelectMany(method => method.Parameters)
                    .Any(parameter => CarriesPort(parameter.ParameterType, port)))
                errors.Add("Global-self SQL port must be an InvokeAsync-only typed lazy factory, so unrelated routes never construct its owner: " + port);
        RequireCalls(census.Body(Entry, "InvokeAsync"), errors,
            (Auth + "GlobalSelfOperation", "ForPath"), (Auth + "HttpIdentitySelfEntryAdmission", "CurrentSubject"),
            ("LearnStack.Modules.Identity.Application.Contracts.Entry.IIdentitySelfStateReader", "ReadSelfAsync"),
            ("LearnStack.Modules.Identity.Application.Contracts.Entry.IIdentitySelfBootstrapper", "BootstrapAsync"));
        RequireCalls(census.Body(Entry, "ContextsAsync"), errors,
            ("LearnStack.Modules.Identity.Application.Contracts.Entry.IIdentitySelfDiscovery", "ReadScopesAsync"),
            ("LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle.IOwnMembershipScopeLabels", "ReadAsync"));
        return errors;
    }

    private static bool IsLazyPort(TypeReference type, string port) => type is GenericInstanceType factory
        && factory.ElementType.FullName == "System.Func`1" && factory.GenericArguments.Count == 1
        && factory.GenericArguments[0].FullName == port;

    private static bool CarriesPort(TypeReference type, string port) =>
        Il.ExpandedTypeReferences(type).Any(reference => reference.FullName == port);

    private static List<string> DiscoveryOffenders(Census census)
    {
        var errors = new List<string>();
        var witness = census.Type(Witness);
        if (!witness.IsSealed || !witness.Methods.Single(method => method.IsConstructor).IsPrivate
            || witness.Properties.Any(property => property.SetMethod is not null)) errors.Add("Own scope has private immutable construction");
        Set(errors, "scope minters", census.Types.Where(type => Derives(census, type, Minter)).Select(type => type.FullName), [ScopeSource]);
        RequireCallers(census, errors, Witness, ".ctor", [Minter + ".Capture"]);
        RequireCallers(census, errors, Minter, "Capture", [ScopeSource + ".CaptureScope"]);
        RequireCallers(census, errors, Witness, "TryBindTo", [Unit + ".BeginSelfDiscoveryTransactionAsync"]);
        RequireCallers(census, errors, UnitPort, "BeginSelfDiscoveryTransactionAsync", [Labels + ".ReadAsync"]);
        RequireCallers(census, errors, Unit, "BeginSelfDiscoveryTransactionAsync", []);
        RequireCalls(census.Body(Witness, "TryBindTo"), errors, ("System.Threading.Interlocked", "CompareExchange"));
        RequireCalls(census.Body(Minter, "Dispose"), errors, ("System.Threading.Interlocked", "Exchange"));
        var owner = census.Body(Unit, "BeginSelfDiscoveryTransactionAsync");
        RequireCalls(owner, errors, (Witness, "TryBindTo"), (Witness, "IsBoundTo"),
            (Unit, "BeginTransactionCoreAsync"), (Unit, "RollbackCoreAsync"));
        var begin = Calls(owner, Unit, "BeginTransactionCoreAsync").ToArray();
        if (begin.Length != 1 || Previous(begin[0], 4).OpCode != OpCodes.Ldc_I4_1 || Previous(begin[0], 3).OpCode != OpCodes.Ldc_I4_0)
            errors.Add("Self discovery opens physically ReadOnly, non-global on the common unit");
        var announcement = Strings(owner).Select(item => item.Text).SingleOrDefault(text => text.Contains("set_config('app.tenant_id'", StringComparison.Ordinal));
        if (announcement is null || !announcement.Contains("set_config('app.organization_id', @organization, true)", StringComparison.Ordinal)
            || ClearedAuthorityKeys
                .Any(key => !announcement.Contains("set_config('app." + key + "', '', true)", StringComparison.Ordinal)))
            errors.Add("Captured scope announces tenant/org and clears every unrelated authority GUC");
        var reader = census.Body(Labels, "ReadAsync");
        RequireCalls(reader, errors, (UnitPort, "BeginSelfDiscoveryTransactionAsync"), (UnitPort, "IsSelfDiscoveryOwner"),
            ("LearnStack.SharedKernel.Persistence.IUnitOfWorkScope", "CompleteAsync"),
            ("System.Threading.CancellationTokenSource", "CancelAfter"));
        var sql = Strings(reader).Select(item => item.Text).SingleOrDefault(text => text.Contains("FROM public.tenants", StringComparison.Ordinal));
        if (sql is null) errors.Add("The actual labels statement must remain present");
        else
        {
            Set(errors, "label tables", Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+public\.([a-z_]+)", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal), ["tenants", "organizations", "platform_host_to_tenant"]);
            if (!sql.Contains("transaction_read_only", StringComparison.Ordinal) || !sql.Contains("transaction_isolation", StringComparison.Ordinal)
                || !sql.Contains("WHERE t.id=@tenant", StringComparison.Ordinal)
                || !sql.Contains("o.tenant_id=t.id AND o.id=@organization", StringComparison.Ordinal))
                errors.Add("Label SQL verifies actual mode and exact captured tenant/organization");
        }
        if (Il.ReferencedTypeNames(census.Type(Labels)).Any(name => name is "Npgsql.NpgsqlDataSource" or "System.IServiceProvider"
            or "LearnStack.SharedKernel.Permissions.AuthorizationCandidate" or "LearnStack.Modules.Identity.Domain.MembershipProfile"
            or "LearnStack.Modules.Identity.Domain.Role")) errors.Add("Label reader has no independent pool, permission or Identity profile capability");
        foreach (var statement in Strings(reader).Select(item => item.Text).Where(text => text.StartsWith("SELECT", StringComparison.Ordinal)))
            if (Regex.IsMatch(statement, @"\b(?:INSERT|UPDATE|DELETE|pg_advisory\w*|fn_identity_\w*)\b", RegexOptions.IgnoreCase))
                errors.Add("Label reader cannot write, lock business facts or invoke global identity functions");
        return errors;
    }

    private sealed record Descriptor(int Kind, string Path, string Method);
    private static List<string> DescriptorOffenders(IEnumerable<Descriptor> descriptors)
    {
        Descriptor[] expected = [new(1, "/api/v1/identity/self", "GET"), new(2, "/api/v1/identity/self/bootstrap", "POST"), new(3, "/api/v1/identity/self/contexts", "GET")];
        return descriptors.OrderBy(item => item.Kind).SequenceEqual(expected) ? [] : ["Global-self descriptor union changed"];
    }

    private static List<string> PipelineOffenders(string program)
    {
        var withoutComments = Regex.Replace(program, @"//[^\r\n]*|/\*[\s\S]*?\*/", string.Empty);
        string[] expected = ["UseAuthentication", "UseLearnStackTenantCredentialAdmission", "UseLearnStackTenantSubjectAdmission",
            "UseLearnStackNativeSessionSource", "UseMiddleware<GlobalSelfEntryMiddleware>", "UseLearnStackTenantIdentityBindingAdmission",
            "UseLearnStackTenantResolution", "UseLearnStackTenantAssertions", "UseLearnStackOperationClassification", "UseAuthorization", "UseLearnStackTenantSurfaceAdmission"];
        var actual = Regex.Matches(withoutComments, @"\bapp\.(Use\w+(?:<[^>]+>)?)\s*\(")
            .Select(match => match.Groups[1].Value).Where(expected.Contains).ToArray();
        return actual.SequenceEqual(expected, StringComparer.Ordinal) ? [] : ["The quota/native/global-self/binding/resolution order changed"];
    }

    private static bool Derives(Census census, TypeDefinition type, string parent)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = census.Types.SingleOrDefault(item => item.FullName == baseType.FullName)?.BaseType)
            if (baseType.FullName == parent) return true;
        return false;
    }
    private static void RequireFields(MethodDefinition method, List<string> errors, params string[] fields)
    {
        var actual = method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldfld)
            .Select(instruction => ((FieldReference)instruction.Operand).Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in fields) if (!actual.Contains(field)) errors.Add("Missing captured evidence check: " + method.FullName + ":" + field);
    }
    private static void RequireCalls(MethodDefinition method, List<string> errors, params (string Type, string Method)[] expected)
    {
        foreach (var (type, name) in expected) if (!Calls(method, type, name).Any()) errors.Add("Missing guarded call: " + method.FullName + " -> " + type + "." + name);
    }
    private static void RequireCallers(Census census, List<string> errors, string type, string name, string[] expected) =>
        Set(errors, "callers of " + type + "." + name, census.Bodies.SelectMany(item => Calls(item.Body, type, name).Select(_ => item.Owner + "." + item.Name)), expected);
    private static void Set(List<string> errors, string description, IEnumerable<string> actual, IEnumerable<string> expected)
    {
        if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)) errors.Add("Unexpected " + description);
    }
    private static IEnumerable<Instruction> Calls(MethodDefinition method, string type, string name) => method.Body.Instructions
        .Where(instruction => instruction.OpCode.FlowControl == FlowControl.Call && instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == type && target.Name == name);
    private static IEnumerable<(Instruction Instruction, string Text)> Strings(MethodDefinition method) => method.Body.Instructions
        .Where(instruction => instruction.OpCode == OpCodes.Ldstr).Select(instruction => (instruction, (string)instruction.Operand));
    private static Instruction Previous(Instruction instruction, int count)
    {
        for (var index = 0; index < count; index++) instruction = instruction.Previous!;
        return instruction;
    }
    private static Action RemoveCall(MethodDefinition method, string type, string name) => Replace(Calls(method, type, name).First(), OpCodes.Nop, null);
    private static Action Replace(Instruction instruction, OpCode code, object? operand)
    {
        var oldCode = instruction.OpCode;
        var oldOperand = instruction.Operand;
        instruction.OpCode = code;
        instruction.Operand = operand;
        return () => { instruction.OpCode = oldCode; instruction.Operand = oldOperand; };
    }
    private static Action AddCall(ModuleDefinition module, MethodReference target, string? existingType = null)
    {
        var type = existingType is null ? new TypeDefinition("NativeControl", "BorrowedEvidence", Mono.Cecil.TypeAttributes.NotPublic, module.TypeSystem.Object)
            : module.GetType(existingType);
        var method = new MethodDefinition("PlantedBorrow", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, module.ImportReference(target)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        if (existingType is null) module.Types.Add(type);
        return () => { type.Methods.Remove(method); if (existingType is null) module.Types.Remove(type); };
    }
    private static void Dirty(Census census, Func<Census, List<string>> detector, Func<Action> mutate)
    {
        var restore = mutate();
        try { detector(census).Should().NotBeEmpty("the production detector rejects the deliberately planted boundary violation"); }
        finally { restore(); }
        detector(census).Should().BeEmpty("the restored compiled positive control remains valid");
    }
    private sealed class Census : IDisposable
    {
        private readonly ModuleDefinition[] _modules;
        internal ModuleDefinition[] Modules => _modules;
        internal static Census Borrow(ModuleDefinition[] modules) => new(modules);
        private Census(ModuleDefinition[] modules) => _modules = modules;
        internal IEnumerable<TypeDefinition> Types => _modules.SelectMany(module => module.GetTypes());
        internal IEnumerable<(string Owner, string Name, MethodDefinition Body)> Bodies => Types.Where(type => !type.Name.Contains('<', StringComparison.Ordinal))
            .SelectMany(type => Il.Methods(type).Where(item => item.Definition.HasBody)
                .Select(item => (type.FullName, item.DeclaredAs, item.Definition)));
        internal TypeDefinition Type(string name) => Types.Single(type => type.FullName == name);
        internal MethodDefinition Body(string type, string name) => Il.Methods(Type(type)).Where(item => item.DeclaredAs == name && item.Definition.HasBody)
            .Select(item => item.Definition).OrderByDescending(method => method.Name == "MoveNext").First();
        internal static Census Read() => new([.. ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location))]);
        public void Dispose() { foreach (var module in _modules) module.Dispose(); }
    }
}
