using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Census = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0081's separate completion census; ADR-0080's existing fact owns the shared transition and pristine census.</summary>
public sealed class SeedOriginCompletionConventionTests
{
    private const string Witness = "LearnStack.SharedKernel.Permissions.SeedCompletionReadWitness";
    private const string Minter = Witness + "/MintingSource";
    private const string Source = "LearnStack.Tools.Seeder.FixedSeedCompletionSource";
    private const string Verifier = "LearnStack.Tools.Seeder.FixedSeedCompletionVerifier";
    private const string Operation = "LearnStack.Tools.Seeder.FixedSeedCompletionReadOperation";
    private const string Resolver = "LearnStack.Tools.Seeder.FixedSeedCompletionReadOperationResolver";
    private const string Unit = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
    private const string Adapter = Unit + "/CompletionSeedRead";
    private const string UnitPort = "LearnStack.SharedKernel.Persistence.IFixedSeedPrerequisiteUnitOfWork";
    private const string OperationPort = "LearnStack.SharedKernel.Persistence.ISeedCompletionReadOperation";
    private const string ResolverPort = "LearnStack.SharedKernel.Persistence.ISeedCompletionReadOperationResolver";
    private const string Tenancy = "LearnStack.Modules.Tenancy.Application.Contracts.Seeding.";
    private const string Customization = "LearnStack.Modules.Customization.Application.Contracts.Seeding.";
    private const string Education = "LearnStack.Modules.Education.Application.Contracts.Seeding.";
    private const string Identity = "LearnStack.Modules.Identity.Application.Contracts.Seeding.";
    private const string Prepared = Tenancy + "SeedAdministrationOriginPreparedTarget";
    private const string Mapping = Tenancy + "SeedAdministrationOriginPreparedMapping";
    private const string VerifierPort = Tenancy + "IFixedSeedOriginCompletionVerifier";
    private const string Deny = Tenancy + "DenyAllFixedSeedOriginCompletionVerifier";
    private const string Command = Tenancy + "AdoptSeedAdministrationOriginCommand";
    private const string Handler = "LearnStack.Modules.Tenancy.Application.Seeding.AdoptSeedAdministrationOriginCommandHandler";
    private const string TenantReader = "LearnStack.Modules.Tenancy.Infrastructure.Persistence.FixedSeedTenantCompletionReader";
    private const string CustomizationReader = "LearnStack.Modules.Customization.Infrastructure.Persistence.FixedSeedCustomizationCompletionReader";
    private const string EducationReader = "LearnStack.Modules.Education.Infrastructure.Persistence.FixedSeedEducationCompletionReader";
    private const string IdentityReader = "LearnStack.Modules.Identity.Infrastructure.Persistence.FixedSeedIdentityCompletionReader";
    private const string Composition = "LearnStack.Tools.Seeder.SeedComposition::Build";
    private static readonly string[] DeclarationTypes = [Tenancy + "SeedTenantDeclaration", Customization + "SeedCustomizationDeclaration",
        Education + "SeedEducationDeclaration", Identity + "SeedIdentityCompletionDeclaration"];
    private static readonly string[] Readers = [TenantReader, CustomizationReader, EducationReader, IdentityReader];
    private static readonly string[] ReaderPorts = [Tenancy + "ISeedTenantCompletionReader", Customization + "ISeedCustomizationCompletionReader",
        Education + "ISeedEducationCompletionReader", Identity + "ISeedIdentityCompletionReader"];
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["source"] = "LearnStack.Tools.Seeder/FixedSeedCompletionSource.cs",
        ["bridge"] = "LearnStack.Tools.Seeder/FixedSeedCompletionVerifier.cs",
        ["handler"] = "Modules/Tenancy/LearnStack.Modules.Tenancy.Application/Seeding/AdoptSeedAdministrationOriginCommandHandler.cs",
        ["tenant"] = "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/FixedSeedTenantCompletionReader.cs",
        ["customization"] = "Modules/Customization/LearnStack.Modules.Customization.Infrastructure/Persistence/FixedSeedCustomizationCompletionReader.cs",
        ["education"] = "Modules/Education/LearnStack.Modules.Education.Infrastructure/Persistence/FixedSeedEducationCompletionReader.cs",
        ["identity"] = "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/FixedSeedIdentityCompletionReader.cs",
        ["runner"] = "LearnStack.Tools.Seeder/SeedAdministrationOriginRunner.cs",
        ["warm"] = "LearnStack.Tools.Seeder/SeedVerification.cs",
        ["api"] = "LearnStack.Api/Composition/PersistenceCompositionExtensions.cs",
        ["manifest"] = "Modules/Tenancy/LearnStack.Modules.Tenancy.Application.Contracts/Seeding/SeedAdministrationOriginManifest.cs",
        ["ddl"] = "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/SeedAdministrationOriginDdl.cs",
        ["migration"] = "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/Migrations/20261029045617_qualify_seed_administration_origins.cs",
    };

    private static readonly Projection[] Projections =
    [
        new("MembershipMetadata", "Id TenantId OrganizationId UserId Status Version CreatedAt CreatedBy UpdatedAt UpdatedBy DeletedAt DeletedBy", """
            SELECT m.id AS "Id", m.tenant_id AS "TenantId", m.organization_id AS "OrganizationId", m.user_id AS "UserId",
                   m.status AS "Status", m.row_version AS "Version", m.created_at AS "CreatedAt", m.created_by AS "CreatedBy",
                   m.updated_at AS "UpdatedAt", m.updated_by AS "UpdatedBy", m.deleted_at AS "DeletedAt", m.deleted_by AS "DeletedBy"
              FROM public.memberships AS m
             WHERE m.tenant_id = @tenant AND (m.id = ANY(@ids)
                OR m.organization_id IS NULL AND m.status = 'Active' AND m.deleted_at IS NULL AND m.deleted_by IS NULL
                   AND EXISTS (SELECT 1 FROM public.membership_role_assignments AS a
                         JOIN public.roles AS r ON r.tenant_id = a.tenant_id AND r.id = a.role_id
                        WHERE a.tenant_id = m.tenant_id AND a.membership_id = m.id AND a.organization_id IS NULL
                          AND r.built_in_key = 'TenantAdmin' AND r.permission_scope = 'Tenant'
                          AND r.deleted_at IS NULL AND r.deleted_by IS NULL))
             ORDER BY m.id
            """),
        new("ProfileMetadata", "MembershipId OrganizationId", """
            SELECT membership_id AS "MembershipId", organization_id AS "OrganizationId"
              FROM public.membership_profiles WHERE tenant_id = @tenant AND membership_id = ANY(@ids)
             ORDER BY membership_id
            """),
        new("AssignmentMetadata", "MembershipId OrganizationId RoleId", """
            SELECT membership_id AS "MembershipId", organization_id AS "OrganizationId", role_id AS "RoleId"
              FROM public.membership_role_assignments WHERE tenant_id = @tenant AND membership_id = ANY(@ids)
             ORDER BY membership_id, role_id
            """),
        new("RoleMetadata", "Id TenantId BuiltInKey Scope Version CreatedAt CreatedBy UpdatedAt UpdatedBy DeletedAt DeletedBy ReleaseVersion ReleaseFingerprint", """
            SELECT id AS "Id", tenant_id AS "TenantId", built_in_key AS "BuiltInKey", permission_scope AS "Scope",
                   row_version AS "Version", created_at AS "CreatedAt", created_by AS "CreatedBy",
                   updated_at AS "UpdatedAt", updated_by AS "UpdatedBy", deleted_at AS "DeletedAt", deleted_by AS "DeletedBy",
                   adopted_release_version AS "ReleaseVersion", adopted_release_fingerprint AS "ReleaseFingerprint"
              FROM public.roles WHERE tenant_id = @tenant AND id = ANY(@ids) ORDER BY id
            """),
        new("GrantMetadata", "RoleId Scope PermissionKey", """
            SELECT role_id AS "RoleId", permission_scope AS "Scope", permission_key AS "PermissionKey"
              FROM public.role_permission_grants WHERE tenant_id = @tenant AND role_id = ANY(@ids)
             ORDER BY role_id, permission_scope, permission_key
            """),
    ];

    [Fact]
    public void Fixed_Seed_Origin_Adoption_Requires_Completed_Foundation()
    {
        using var census = Census.Read();
        var sources = Paths.ToDictionary(pair => pair.Key,
            pair => File.ReadAllText(Path.Combine(SourceScan.SourceRoot, pair.Value)), StringComparer.Ordinal);
        AssertClean(census, sources);
        Dirty(census, sources, () =>
        {
            var type = census.Type(Witness);
            var constructor = type.Methods.Single(method => method.IsConstructor);
            var attributes = constructor.Attributes;
            constructor.IsPublic = true;
            return () => constructor.Attributes = attributes;
        });
        Dirty(census, sources, () =>
        {
            var module = census.Type(Source).Module;
            var extra = new TypeDefinition("CompletionControl", "ExtraMinter", TypeAttributes.NotPublic, module.ImportReference(census.Type(Minter)));
            module.Types.Add(extra);
            return () => module.Types.Remove(extra);
        });
        Dirty(census, sources, () => RemoveCall(census.Body(Source, "Capture"), Minter, "ForCompletionRead"));
        Dirty(census, sources, () => RemoveCall(census.Body(EducationReader, "RequireCurrent"), Witness, "IsBoundTo"));
        Dirty(census, sources, () => AddCaller(census, census.Method(UnitPort, "VerifySeedCompletionAsync")));
        Dirty(census, sources, () => AddCaller(census, census.Type(Source).Module.ImportReference(
            typeof(System.Reflection.MethodBase).GetMethod(nameof(System.Reflection.MethodBase.Invoke), [typeof(object), typeof(object[])])!)));
        Dirty(census, sources, () => CorruptExpressionMetadata(census, replaceToken: true));
        Dirty(census, sources, () => CorruptExpressionMetadata(census, replaceToken: false));
        DirtySource(census, sources, "source", text => text.Replace("ReferenceEquals(policy, invocation.Policy)", "true", StringComparison.Ordinal));
        DirtySource(census, sources, "source", text => text.Replace("_seedSource.Owns(source, invocation.Operation.GetType(), _context)", "true", StringComparison.Ordinal));
        DirtySource(census, sources, "source", text => text.Replace("prepared.TenantVersion == request.ExpectedVersion", "prepared.TenantVersion > 0", StringComparison.Ordinal));
        DirtySource(census, sources, "handler", text => text.Replace("tenant.Version, tenant.AdoptedSeedOriginVersion, tenant.DefaultOrganizationId,",
            "request.ExpectedVersion, tenant.AdoptedSeedOriginVersion, tenant.DefaultOrganizationId,", StringComparison.Ordinal));
        DirtySource(census, sources, "handler", text => text.Replace("if (!await completion.VerifyAsync(request, prepared, plan, cancellationToken))", "if (false)", StringComparison.Ordinal));
        DirtySource(census, sources, "bridge", text => text.Replace("ISeedTenantCompletionReader", "ISeedEducationCompletionReader", StringComparison.Ordinal));
        DirtySource(census, sources, "identity", text => text.Replace("SELECT membership_id AS", "SELECT display_name AS \"DisplayName\", membership_id AS", StringComparison.Ordinal));
        DirtySource(census, sources, "runner", text => text.Replace("before.AdoptedSeedOriginVersion == 2", "before.AdoptedSeedOriginVersion == 1", StringComparison.Ordinal));
        DirtySource(census, sources, "ddl", text => text.Replace("OLD.adopted_seed_origin_version NOT IN (0,1)", "OLD.adopted_seed_origin_version NOT IN (0,1,2)", StringComparison.Ordinal));
    }

    private static List<string> Offenders(Census census, Dictionary<string, string> sources)
    {
        var errors = new List<string>();
        var references = census.Methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => new Reference(method, (MethodReference)instruction.Operand, instruction))).ToArray();
        var witness = census.Type(Witness);
        if (!witness.IsPublic || !witness.IsSealed || witness.Methods.Count(method => method.IsConstructor) != 1
            || !witness.Methods.Single(method => method.IsConstructor).IsPrivate
            || witness.Properties.Any(property => property.SetMethod is not null)
            || witness.Fields.Any(field => !field.IsPrivate || field.IsStatic || !field.IsInitOnly && field.Name != "_binding")
            || witness.Interfaces.Count != 0)
            errors.Add("Completion authority remains a distinct sealed immutable witness with private construction");
        if (!witness.Fields.Any(field => field.Name == "_declarations"
                && field.FieldType.GetElementType().FullName == "System.Collections.Immutable.ImmutableDictionary`2"))
            errors.Add("The completion witness retains immutable per-module declarations");
        ExactTypes(census.Types.Where(type => type.BaseType?.FullName == Minter), [Source], "completion minters");
        Implementations(VerifierPort, [Verifier, Deny]);
        Implementations(OperationPort, [Operation]);
        Implementations(ResolverPort, [Resolver]);
        for (var index = 0; index < Readers.Length; index++) Implementations(ReaderPorts[index], [Readers[index]]);
        Exact(Witness, ".ctor", [Minter + "::ForCompletionRead"]);
        Exact(Minter, "ForCompletionRead", [Source + "::Capture"]);
        Exact(Source, "Capture", [Verifier + "::VerifyAsync"]);
        Exact(Source, "CaptureDeclarations", [Source + "::.ctor"]);
        Exact(VerifierPort, "VerifyAsync", [Handler + "::Handle"]);
        Exact(UnitPort, "VerifySeedCompletionAsync", [Verifier + "::VerifyAsync"]);
        Exact(Unit, "VerifySeedCompletionAsync", []);
        Exact(OperationPort, "ExecuteAsync", [Adapter + "::ExecuteAsync"]);
        Exact(ResolverPort, "Resolve", [Adapter + "::Resolve"]);
        Exact(Witness, "TryBindTo", [Adapter + "::TryBindTo"]);
        Exact(Witness, "IsExpectedOn", [Witness + "::TryBindTo", TenantReader + "::RequireCurrent", CustomizationReader + "::RequireCurrent"]);
        Exact(Witness, "IsBoundTo", [Adapter + "::IsBoundTo", EducationReader + "::RequireCurrent", IdentityReader + "::RequireCurrent"]);
        Exact(Witness, "GetPreparedTarget", [Source + "::IsCurrent", TenantReader + "::VerifyAsync"]);
        Exact(Witness, "RequireCurrent", [Adapter + "::RequireCurrent", TenantReader + "::RequireCurrent", CustomizationReader + "::RequireCurrent",
            EducationReader + "::RequireCurrent", IdentityReader + "::RequireCurrent", Verifier + "::VerifyAsync", Verifier + "::VerifyAsync",
            Verifier + "::VerifyAsync", Verifier + "::VerifyAsync", Operation + "::ExecuteAsync", Operation + "::ExecuteAsync", Operation + "::ExecuteAsync"]);
        for (var index = 0; index < ReaderPorts.Length; index++)
            Exact(ReaderPorts[index], "VerifyAsync", [index < 2 ? Verifier + "::VerifyAsync" : Operation + "::ExecuteAsync"]);
        foreach (var type in new[] { Prepared, Mapping })
        {
            var carrier = census.Type(type);
            if (!carrier.IsSealed || carrier.Properties.Any(property => property.SetMethod is not null)
                || carrier.Fields.Any(field => !field.IsPrivate || !field.IsInitOnly || field.IsStatic))
                errors.Add("Prepared locked target facts remain sealed and immutable: " + type);
            Exact(type, ".ctor", [Handler + "::Handle"]);
        }
        var getterCalls = references.Where(reference => reference.Target.DeclaringType.FullName == Witness && reference.Target.Name == "GetDeclaration");
        var expectedGetters = DeclarationTypes.Select(type => Source + "::IsCurrent|" + type)
            .Concat(Readers.Select((reader, index) => reader + (index == 3 ? "::ReadAsync|" : "::VerifyAsync|") + DeclarationTypes[index]));
        if (!Same(getterCalls.Select(reference => Owner(census, reference.Method) + "|" + GenericArgument(reference.Target)), expectedGetters))
            errors.Add("Only each owning reader and the source can inspect the four exact declaration types");
        if (references.Any(reference => reference.Target.DeclaringType.FullName == Witness && reference.Target.Name == "GetPreparedTarget"
                && GenericArgument(reference.Target) != Prepared)) errors.Add("Prepared evidence cannot escape through an alternate target type");
        foreach (var type in DeclarationTypes)
            if (!census.Type(type).IsSealed || census.Type(type).Fields.Any(field => !field.IsPrivate || !field.IsInitOnly || field.IsStatic))
                errors.Add("Completion declarations remain sealed immutable records: " + type);
        Signature(UnitPort, "VerifySeedCompletionAsync", [Witness, "System.Threading.CancellationToken"], "System.Threading.Tasks.Task`1<System.Boolean>");
        Signature(OperationPort, "ExecuteAsync", [Witness, "System.Threading.CancellationToken"], "System.Threading.Tasks.Task`1<System.Boolean>");
        Signature(ResolverPort, "Resolve", [], OperationPort);
        Signature(VerifierPort, "VerifyAsync", [Command, Prepared, "LearnStack.SharedKernel.Identity.IdentityMutationLockPlan", "System.Threading.CancellationToken"],
            "System.Threading.Tasks.Task`1<System.Boolean>");
        foreach (var port in ReaderPorts) Signature(port, "VerifyAsync", [Witness, "System.Threading.CancellationToken"], "System.Threading.Tasks.Task`1<System.Boolean>");
        foreach (var port in ReaderPorts.Concat([OperationPort, ResolverPort, VerifierPort]))
            if (census.Type(port).Methods.Count != 1 || census.Type(port).Properties.Count != 0 || census.Type(port).Events.Count != 0)
                errors.Add("The closed completion port exposes only its single typed operation: " + port);
        if (!census.Type(Operation).Methods.Single(method => method.IsConstructor).Parameters.Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual([ReaderPorts[2], ReaderPorts[3]])) errors.Add("The wide operation receives exactly Education and Identity through its constructor");
        Constructor(TenantReader, ["LearnStack.Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext",
            "LearnStack.Modules.Tenancy.Application.Abstractions.ISeedStateReader", "LearnStack.SharedKernel.Persistence.IUnitOfWork", "LearnStack.SharedKernel.Tenancy.ITenantContext"]);
        foreach (var (reader, module) in new[] { (CustomizationReader, "Customization"), (EducationReader, "Education"), (IdentityReader, "Identity") })
            Constructor(reader, [$"LearnStack.Modules.{module}.Infrastructure.Persistence.{module}DbContext", "LearnStack.SharedKernel.Persistence.IUnitOfWork"]);
        Require(Witness, "RequireCurrent", "LearnStack.SharedKernel.Identity.IIdentityMutationLocks", "RequireCurrent", 1);
        Require(Source, "Capture", "LearnStack.SharedKernel.Identity.IIdentityMutationLocks", "RequireCurrent", 1);
        Require(Source, "HasInvocation", "LearnStack.Tools.Seeder.SeedOperationSource", "Owns", 1);
        Require(Minter, "Dispose", "System.Threading.Interlocked", "Exchange", 1);
        Require(Handler, "Handle", "LearnStack.SharedKernel.Identity.IIdentityMutationLocks", "AcquireAsync", 1);
        Require(Handler, "Handle", "LearnStack.SharedKernel.Identity.IdentityMutationLockPlan", ".ctor", 1);
        Require(TenantReader, "VerifyAsync", TenantReader, "RequireCurrent", 7);
        Require(CustomizationReader, "VerifyAsync", CustomizationReader, "RequireCurrent", 3);
        Require(EducationReader, "VerifyAsync", EducationReader, "RequireCurrent", 3);
        Require(IdentityReader, "ReadAsync", IdentityReader, "RequireCurrent", 6);
        foreach (var (type, method) in new[] { (Witness, "get_IsActive"), (Witness, "IsExpectedOn"), (Source, "IsCurrent"), (Source, "HasInvocation"), (Source, "Matches") })
            if (Calls(census.Body(type, method)).Any(call => call.DeclaringType.FullName is "LearnStack.SharedKernel.Persistence.IUnitOfWork"
                    or "LearnStack.SharedKernel.Identity.IIdentityMutationLocks")) errors.Add("Lifetime and expected-owner inspection stay pure: " + type + "::" + method);
        CheckRegistrations(census, references, errors);
        CheckConfinement(census, references, errors);
        CheckSource(sources["source"], errors);
        CheckPreparation(sources["handler"], errors);
        CheckOrdering(sources["bridge"], errors);
        CheckReaders(census, sources, errors);
        CheckRepeatAndMigration(sources, errors);
        return errors;

        void ExactTypes(IEnumerable<TypeDefinition> actual, IEnumerable<string> expected, string description)
        { if (!Same(actual.Select(type => type.FullName), expected)) errors.Add("The exact " + description + " changed"); }
        void Implementations(string port, string[] expected) => ExactTypes(census.Types.Where(type => type.Interfaces.Any(item => item.InterfaceType.FullName == port)), expected, port);
        void Exact(string type, string method, string[] expected)
        {
            var found = references.Where(reference => reference.Target.DeclaringType.FullName == type && reference.Target.Name == method).ToArray();
            if (found.Any(reference => reference.Instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj))
                || !Same(found.Select(reference => Owner(census, reference.Method)), expected)) errors.Add("The completion caller/alias census changed: " + type + "::" + method);
        }
        void Signature(string type, string method, string[] parameters, string result)
        {
            var actual = census.Method(type, method);
            if (actual.ReturnType.FullName != result || !actual.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                errors.Add("The completion signature cannot accept submitted work or disclose values: " + type + "::" + method);
        }
        void Require(string type, string method, string target, string name, int count)
        { if (Calls(census.Body(type, method)).Count(call => call.DeclaringType.FullName == target && call.Name == name) != count) errors.Add("Missing executing completion guard: " + type + "::" + method + " -> " + name); }
        void Constructor(string type, string[] parameters)
        {
            var constructors = census.Type(type).Methods.Where(method => method.IsConstructor).ToArray();
            if (constructors.Length != 1 || !constructors[0].Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                errors.Add("Owning completion readers receive only their module state and actual scoped owner: " + type);
        }
    }

    private static void CheckRegistrations(Census census, Reference[] references, List<string> errors)
    {
        var composed = new[] { Source, Verifier, Operation, Resolver }.Concat(Readers).ToArray();
        var actual = references.Where(reference => reference.Target is GenericInstanceMethod generic && generic.GenericArguments.Any(argument => composed.Contains(argument.FullName, StringComparer.Ordinal))
                && reference.Target.Name is "AddScoped" or "TryAddScoped" or "AddSingleton" or "TryAddSingleton" or "AddTransient" or "TryAddTransient" or "Scoped" or "Singleton" or "Transient")
            .Select(reference => Owner(census, reference.Method) + "|" + reference.Target.Name + "|" + string.Join(",", ((GenericInstanceMethod)reference.Target).GenericArguments.Select(argument => argument.FullName)));
        var expected = new[] { Source, VerifierPort + "," + Verifier, Operation, ResolverPort + "," + Resolver }
            .Concat(Readers.Select((reader, index) => ReaderPorts[index] + "," + reader)).Select(registration => Composition + "|AddScoped|" + registration);
        if (!Same(actual, expected)) errors.Add("Only the scoped Seeder root composes the completion producer, bridge, readers and operation");
        var deny = references.Where(reference => reference.Target is GenericInstanceMethod generic && generic.GenericArguments.Any(argument => argument.FullName == Deny)).ToArray();
        if (deny.Length != 1 || deny[0].Target.Name != "TryAddSingleton"
            || !Owner(census, deny[0].Method).StartsWith("LearnStack.Api.Composition.PersistenceCompositionExtensions::", StringComparison.Ordinal))
            errors.Add("The API registers only the deny-all completion verifier");
    }

    private static void CheckConfinement(Census census, Reference[] references, List<string> errors)
    {
        var closed = new[] { Source, Verifier, Operation, Resolver }.Concat(Readers).ToArray();
        var calls = references.Where(reference => closed.Any(type => reference.Method.DeclaringType.FullName == type
            || reference.Method.DeclaringType.FullName.StartsWith(type + "/", StringComparison.Ordinal))).ToArray();
        var resolutions = calls.Where(reference => reference.Target.Name is "GetRequiredService" or "GetService")
            .Select(reference => Owner(census, reference.Method) + "|" + GenericArgument(reference.Target));
        if (!Same(resolutions, [Verifier + "::VerifyAsync|" + ReaderPorts[0], Verifier + "::VerifyAsync|" + ReaderPorts[1], Resolver + "::Resolve|" + Operation]))
            errors.Add("Completion resolves only its same-scope owning readers and closed operation");
        foreach (var reference in calls)
        {
            var target = reference.Target;
            if (target.Name is "Send" or "Publish" or "BeginTransactionAsync" or "BeginGlobalIdentityTransactionAsync" or "BeginSelfDiscoveryTransactionAsync"
                or "SetTenantContextAsync" or "SetProvisioningTenantContextAsync" or "AcquireAsync" or "IgnoreQueryFilters" or "CreateCommand"
                or "ExecuteNonQuery" or "ExecuteNonQueryAsync" or "ExecuteScalar" or "ExecuteScalarAsync" or "ExecuteSqlRawAsync"
                or "ExecuteSqlInterpolatedAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync" or "SaveChanges" or "SaveChangesAsync"
                or "CreateScope" or "CreateAsyncScope" or "Open" or "OpenAsync"
                || target.DeclaringType.FullName.StartsWith("System.Net.Http.", StringComparison.Ordinal)
                || target.DeclaringType.FullName.StartsWith("Microsoft.Extensions.Logging.", StringComparison.Ordinal)
                || target.DeclaringType.FullName.StartsWith("LearnStack.SharedKernel.Caching.", StringComparison.Ordinal)
                || target.DeclaringType.FullName == "Npgsql.NpgsqlDataSource"
                || target.DeclaringType.FullName == "Npgsql.NpgsqlConnection"
                || target.DeclaringType.FullName.StartsWith("System.Reflection.", StringComparison.Ordinal)
                    && !IsQueryExpressionMetadata(census, reference)
                || target.DeclaringType.FullName.Contains("GlobalIdentityDbContext", StringComparison.Ordinal)
                || target.DeclaringType.FullName.Contains("IIdentityProvider", StringComparison.Ordinal))
                errors.Add("Completion cannot create an owner, dispatch, mutate or publish inspected evidence: " + Owner(census, reference.Method) + " -> " + target.FullName);
            if (target.Name is "SqlQueryRaw" or "SqlQuery" or "FromSqlRaw" or "FromSqlInterpolated"
                && (target.Name != "SqlQueryRaw" || Owner(census, reference.Method) != IdentityReader + "::Query"))
                errors.Add("Only the fixed Identity metadata helper submits completion SQL");
        }
    }

    // C# emits these literal-handle conversions while building EF expressions.
    // They describe an owning query member; they neither invoke reflection nor
    // leave a MethodInfo/FieldInfo available for later caller-selected work.
    private static bool IsQueryExpressionMetadata(Census census, Reference reference)
    {
        var owner = Owner(census, reference.Method);
        var reader = owner == CustomizationReader + "::VerifyAsync" ? CustomizationReader
            : owner == EducationReader + "::VerifyAsync" ? EducationReader : null;
        if (reader is null || reference.Instruction.OpCode != OpCodes.Call) return false;
        var target = reference.Target;
        var methodHandle = target.DeclaringType.FullName == "System.Reflection.MethodBase" && target.Name == "GetMethodFromHandle"
            && target.ReturnType.FullName == "System.Reflection.MethodBase";
        var fieldHandle = target.DeclaringType.FullName == "System.Reflection.FieldInfo" && target.Name == "GetFieldFromHandle"
            && target.ReturnType.FullName == "System.Reflection.FieldInfo";
        if ((!methodHandle && !fieldHandle) || target.HasThis || target.Parameters.Count is not (1 or 2)
            || target.Parameters[0].ParameterType.FullName != (methodHandle ? "System.RuntimeMethodHandle" : "System.RuntimeFieldHandle")) return false;
        var token = Previous(reference.Instruction);
        TypeReference? declaringHandle = null;
        if (target.Parameters.Count == 2)
        {
            if (target.Parameters[1].ParameterType.FullName != "System.RuntimeTypeHandle"
                || token is not { OpCode.Code: Code.Ldtoken, Operand: TypeReference declaring }) return false;
            declaringHandle = declaring;
            token = Previous(token);
        }
        if (token is not { OpCode.Code: Code.Ldtoken }) return false;
        if (methodHandle)
        {
            if (token.Operand is not MethodReference member || !IsQueryMethod(reader, member)
                || declaringHandle is not null && declaringHandle.FullName != member.DeclaringType.FullName) return false;
        }
        else if (token.Operand is not FieldReference field
            || !field.DeclaringType.FullName.StartsWith(reader + "/<>c__DisplayClass", StringComparison.Ordinal)
            || field.Name is not ("declaration" or "contentRoot" or "taxonomyRoot" or "courseId" or "lessonId")
            || declaringHandle is not null && declaringHandle.FullName != field.DeclaringType.FullName) return false;

        var sink = Next(reference.Instruction);
        if (methodHandle)
        {
            if (sink is not { OpCode.Code: Code.Castclass, Operand: TypeReference cast }
                || cast.FullName != "System.Reflection.MethodInfo") return false;
            sink = Next(sink);
        }
        return sink is { OpCode.Code: Code.Call, Operand: MethodReference consumer }
            && consumer.DeclaringType.FullName == "System.Linq.Expressions.Expression"
            && (methodHandle ? consumer.Name is "Property" or "Equal" : consumer.Name == "Field")
            && consumer.Parameters.LastOrDefault()?.ParameterType.FullName == (methodHandle ? "System.Reflection.MethodInfo" : "System.Reflection.FieldInfo");
    }

    private static bool IsQueryMethod(string reader, MethodReference member)
    {
        var module = reader == CustomizationReader ? "Customization" : "Education";
        var declaring = member.DeclaringType.GetElementType().FullName;
        if (declaring == "System.String") return member.Name == "op_Equality" && member.ReturnType.FullName == "System.Boolean"
            && member.Parameters.Count == 2 && member.Parameters.All(parameter => parameter.ParameterType.FullName == "System.String");
        if (declaring == "LearnStack.SharedKernel.Domain.Entity`1")
            return member.Name == "get_Id" && member.Parameters.Count == 0
                && member.DeclaringType is GenericInstanceType { GenericArguments.Count: 1 } entity
                && entity.GenericArguments[0].Namespace == "LearnStack.Modules." + module + ".Domain";
        return (member.DeclaringType.Namespace == "LearnStack.Modules." + module + ".Domain"
                || member.DeclaringType.Namespace == "LearnStack.Modules." + module + ".Application.Contracts.Seeding")
            && (member.Name is "get_Id" or "get_Key" or "get_SchemaVersion" or "get_Items" or "get_Translations"
                or "get_BuiltInContentType" or "get_ContentType" or "get_BuiltInTaxonomy" or "get_Taxonomy"
                    && member.Parameters.Count == 0
                || member.Name == "op_Equality" && member.Parameters.Count == 2 && member.ReturnType.FullName == "System.Boolean");
    }

    private static Instruction? Previous(Instruction instruction)
    {
        var previous = instruction.Previous;
        while (previous?.OpCode == OpCodes.Nop) previous = previous.Previous;
        return previous;
    }

    private static Instruction? Next(Instruction instruction)
    {
        var next = instruction.Next;
        while (next?.OpCode == OpCodes.Nop) next = next.Next;
        return next;
    }

    private static Action CorruptExpressionMetadata(Census census, bool replaceToken)
    {
        var body = census.Body(CustomizationReader, "VerifyAsync");
        var reference = body.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => new Reference(body, (MethodReference)instruction.Operand, instruction))
            .First(candidate => IsQueryExpressionMetadata(census, candidate));
        var instruction = replaceToken ? Previous(reference.Instruction)! : Next(reference.Instruction)!;
        if (replaceToken && reference.Target.Parameters.Count == 2) instruction = Previous(instruction)!;
        var opcode = instruction.OpCode;
        var operand = instruction.Operand;
        if (replaceToken) instruction.Operand = census.Method(UnitPort, "VerifySeedCompletionAsync");
        else { instruction.OpCode = OpCodes.Pop; instruction.Operand = null; }
        return () => { instruction.OpCode = opcode; instruction.Operand = operand; };
    }

    private static void CheckSource(string text, List<string> errors)
    {
        var root = Parse(text);
        foreach (var expression in new[]
        {
            "ReferenceEquals(invocation!.Operation, request)", "ReferenceEquals(invocation, _execution.CurrentInvocation)",
            "ReferenceEquals(invocation.Context, _context)", "ReferenceEquals(policy, invocation.Policy)",
            "invocation.Operation.GetType() == typeof(AdoptSeedAdministrationOriginCommand)",
            "_seedSource.Owns(source, invocation.Operation.GetType(), _context)", "ReferenceEquals(witness.Invocation, _invocation)",
            "ReferenceEquals(witness.Plan, _plan)", "ReferenceEquals(witness.GetPreparedTarget<SeedAdministrationOriginPreparedTarget>(), prepared)",
            "prepared.TenantVersion == request.ExpectedVersion", "prepared.TenantVersion > 0", "prepared.Marker is 0 or 1",
            "plan.UserIds.IsEmpty", "plan.TenantMode == IdentityTenantLockMode.Exclusive", "prepared.Mapping is null ? prepared.Marker == 0 : prepared.Mapping.Host == declaration.Host && prepared.Mapping.TenantId == declaration.TenantId && prepared.Mapping.OrganizationId is null && prepared.Mapping.IsActive && prepared.Mapping.IsPubliclyLive",
            "_unit is not IOperationReadScopeView { IsTenantWideRead: false }", "announcement.OrganizationId is not null",
            "new FixedSeedTenantContinuationSource()", "new FixedSeedContentContinuationSource()", "IdentityFoundationManifest.Version1",
            "new PermissionDefinition(grant.Scope, grant.Key)", "SeedData.All", "_attempted = true",
        }) Need(root, expression, errors);
        var capture = Method(root, "CaptureDeclarations");
        if (!Same(capture.DescendantNodes().OfType<TypeOfExpressionSyntax>().Select(node => node.Type.ToString()), DeclarationTypes.Select(Short)))
            errors.Add("The source captures exactly the four owning declaration map keys");
        foreach (var type in DeclarationTypes.Select(Short))
            Need(root, $"ReferenceEquals(witness.GetDeclaration<{type}>(), declaration[typeof({type})])", errors);
    }

    private static void CheckPreparation(string text, List<string> errors)
    {
        var root = Method(Parse(text), "Handle");
        var constructor = "new SeedAdministrationOriginPreparedTarget(tenant.Id, tenant.Slug, tenant.Version, tenant.AdoptedSeedOriginVersion, tenant.DefaultOrganizationId, mapping is null ? null : new SeedAdministrationOriginPreparedMapping(mapping.Host, mapping.TenantId, mapping.OrganizationId, mapping.IsActive, mapping.IsPubliclyLive))";
        Need(root, constructor, errors);
        Need(root, "tenant.AdoptedSeedOriginVersion is not (0 or 1)", errors);
        Need(root, "tenant.AdoptedSeedOriginVersion == 1 && mapping is null", errors);
        Need(root, "tenant.Version != prepared.TenantVersion", errors);
        Need(root, "tenant.AdoptedSeedOriginVersion != prepared.Marker", errors);
        Ordered(root, ["locks.AcquireAsync(plan, cancellationToken)", "tenants.FindForUpdateAsync(context.TenantId, cancellationToken)",
            "mappings.FindForUpdateAsync(declaration.Host, cancellationToken)", constructor,
            "completion.VerifyAsync(request, prepared, plan, cancellationToken)", "mappings.AddAsync(mapping, cancellationToken)",
            "tenant.AdoptSeedAdministrationOrigin(clock, UserId.SystemActor)", "tenants.UpdateAsync(tenant, cancellationToken)"], errors);
    }

    private static void CheckOrdering(string text, List<string> errors)
    {
        var root = Parse(text);
        var bridge = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "FixedSeedCompletionVerifier");
        Ordered(bridge, ["source.Capture(request, prepared, plan)",
            "services.GetRequiredService<ISeedTenantCompletionReader>().VerifyAsync(witness, cancellationToken)",
            "services.GetRequiredService<ISeedCustomizationCompletionReader>().VerifyAsync(witness, cancellationToken)",
            "owner.VerifySeedCompletionAsync(witness, cancellationToken)"], errors);
        var operation = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "FixedSeedCompletionReadOperation");
        Ordered(operation, ["education.VerifyAsync(witness, cancellationToken)", "identity.VerifyAsync(witness, cancellationToken)"], errors);
        Need(root, "services.GetRequiredService<FixedSeedCompletionReadOperation>()", errors);
    }

    private static void CheckReaders(Census census, Dictionary<string, string> sources, List<string> errors)
    {
        foreach (var (key, wide) in new[] { ("tenant", false), ("customization", false), ("education", true), ("identity", true) })
        {
            var root = Parse(sources[key]);
            var guard = Method(root, "RequireCurrent");
            Need(guard, "witness.RequireCurrent()", errors);
            Need(guard, "ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)", errors);
            Need(guard, wide ? "witness.IsBoundTo(unit, transaction, announcement.Generation)" : "witness.IsExpectedOn(unit, transaction, announcement.Generation)", errors);
            Need(guard, "unit.Mode != TransactionMode.ReadWrite", errors);
            Need(guard, "unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }", errors);
            Need(guard, key == "customization" ? "unit is IOperationReadScopeView { IsTenantWideRead: true }"
                : wide ? "unit is not IOperationReadScopeView { IsTenantWideRead: true }" : "unit is not IOperationReadScopeView { IsTenantWideRead: false }", errors);
        }
        var identity = Parse(sources["identity"]);
        var read = Method(identity, "ReadAsync");
        var queries = read.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression is GenericNameSyntax { Identifier.ValueText: "Query" }).ToArray();
        if (queries.Length != Projections.Length) errors.Add("Identity completion retains exactly five fixed metadata SQL statements");
        foreach (var projection in Projections)
        {
            var found = queries.Where(call => ((GenericNameSyntax)call.Expression).TypeArgumentList.Arguments.Single().ToString() == projection.Metadata).ToArray();
            if (found.Length != 1 || found[0].ArgumentList.Arguments.Count != 3
                || found[0].ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal
                || literal.Token.Value is not string sql || Normalize(sql) != Normalize(projection.Sql))
                errors.Add("The exact completion metadata projection changed: " + projection.Metadata);
            var carrier = census.Type(IdentityReader + "/" + projection.Metadata);
            if (!carrier.IsNestedPrivate || !carrier.IsSealed || !Same(carrier.Properties.Select(property => property.Name), projection.Properties.Split(' ')))
                errors.Add("Identity metadata cannot grow profile/global/provider values: " + projection.Metadata);
        }
        Need(identity, "required is not null && (row.Version <= 0 || row.OrganizationId != required.OrganizationId?.Value || row.UserId != required.UserId.Value)", errors);
        Need(identity, "manifest.TenantAdminKernel.All(values.Contains)", errors);
        var query = Method(identity, "Query");
        if (!query.Modifiers.Any(SyntaxKind.PrivateKeyword)) errors.Add("Only the fixed reader owns its literal-fed SQL helper");
        Need(query, "db.Database.SqlQueryRaw<T>(sql, new NpgsqlParameter(\"tenant\", witness.TenantId.Value), new NpgsqlParameter(\"ids\", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids })", errors);
    }

    private static void CheckRepeatAndMigration(Dictionary<string, string> sources, List<string> errors)
    {
        var runner = Method(Parse(sources["runner"]), "RunAsync");
        var repeat = runner.DescendantNodes().OfType<IfStatementSyntax>().Where(statement => Equivalent(statement.Condition, "before.AdoptedSeedOriginVersion == 2")).ToArray();
        if (repeat.Length != 1 || !SyntaxFactory.AreEquivalent(repeat[0].Statement,
                SyntaxFactory.ParseStatement("{ await VerifyMappingAsync(context, declaration, cancellationToken); continue; }")))
            errors.Add("Only marker 2 with its exact mapping takes the pre-dispatch write-free repeat");
        Ordered(runner, ["before.AdoptedSeedOriginVersion == 2", "new AdoptSeedAdministrationOriginCommand(before.Version)"], errors);
        Need(runner, "before.AdoptedSeedOriginVersion is not (0 or 1)", errors);
        Need(runner, "after?.AdoptedSeedOriginVersion != 2", errors);
        if (runner.DescendantNodes().OfType<CatchClauseSyntax>().Any()) errors.Add("Unknown COMMIT never triggers automatic recovery/adoption dispatch");
        Need(Parse(sources["warm"]), "row.AdoptedSeedOriginVersion is 0 or 1 or 2", errors);
        var manifest = Parse(sources["manifest"]).DescendantNodes().OfType<LiteralExpressionSyntax>().Where(value => value.Token.Value is string)
            .Select(value => value.Token.ValueText).ToArray();
        if (!Same(manifest, ["01930000-0000-7000-8000-000000000001", "demo-english", "manage.demo-english.learnstack.local",
                "01930000-0000-7000-8000-000000000002", "demo-yoga", "manage.demo-yoga.learnstack.local"]))
            errors.Add("Completion remains confined to the two original fixed Tenant/host declarations");
        var migration = Parse(sources["migration"]);
        Need(Method(migration, "Up"), "SeedAdministrationOriginDdl.InstallQualifiedCompletion(migrationBuilder)", errors);
        if (!Method(migration, "Down").DescendantNodes().OfType<ThrowExpressionSyntax>().Any()) errors.Add("Qualified origin migration has no reset/down path");
        var ddl = Method(Parse(sources["ddl"]), "InstallQualifiedCompletion").ToString();
        foreach (var required in new[] { "CHECK (adopted_seed_origin_version IN (0,1,2))", "OLD.adopted_seed_origin_version NOT IN (0,1) OR NEW.adopted_seed_origin_version<>2",
                     "CREATE OR REPLACE FUNCTION public.fn_tenancy_seed_administration_origin_guard()", "SECURITY INVOKER", "mapping.organization_id IS NULL AND mapping.is_active AND mapping.is_publicly_live", "FOR UPDATE;" })
            if (!ddl.Contains(required, StringComparison.Ordinal)) errors.Add("Missing qualified forward-transition constraint: " + required);
        if (Regex.IsMatch(ddl, @"\b(?:ADD\s+COLUMN|CREATE\s+TABLE|CREATE\s+ROLE|SECURITY\s+DEFINER)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            errors.Add("Qualified completion reuses the existing column and invoker guard");
        Need(Parse(sources["api"]), "services.TryAddSingleton<LearnStack.Modules.Tenancy.Application.Contracts.Seeding.IFixedSeedOriginCompletionVerifier, LearnStack.Modules.Tenancy.Application.Contracts.Seeding.DenyAllFixedSeedOriginCompletionVerifier>()", errors);
    }

    private static void Ordered(SyntaxNode root, string[] expressions, List<string> errors)
    {
        var positions = expressions.Select(text => root.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Where(expression => Equivalent(expression, text))
            .Select(expression => expression.SpanStart).ToArray()).ToArray();
        if (positions.Any(found => found.Length != 1) || !positions.Select(found => found.SingleOrDefault()).SequenceEqual(positions.Select(found => found.SingleOrDefault()).Order()))
            errors.Add("Completion ordering changed: " + string.Join(" -> ", expressions));
    }
    private static void Need(SyntaxNode root, string expression, List<string> errors)
    { if (!root.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(node => Equivalent(node, expression))) errors.Add("Missing completion pin: " + expression); }
    private static bool Equivalent(SyntaxNode node, string expression) => SyntaxFactory.AreEquivalent(node, SyntaxFactory.ParseExpression(expression));
    private static SyntaxNode Parse(string source) => CSharpSyntaxTree.ParseText(source).GetRoot();
    private static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name);
    private static string Short(string type) => type[(type.LastIndexOf('.') + 1)..];
    private static string Normalize(string sql) => Regex.Replace(sql.Trim(), @"\s+", " ");
    private static string GenericArgument(MethodReference method) => method is GenericInstanceMethod { GenericArguments.Count: 1 } generic ? generic.GenericArguments[0].FullName : "untyped";
    private static IEnumerable<MethodReference> Calls(MethodDefinition method) => method.Body.Instructions.Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
        .Select(instruction => instruction.Operand).OfType<MethodReference>();
    private static string Owner(Census census, MethodDefinition method) => census.Owner(method);
    private static bool Same(IEnumerable<string> actual, IEnumerable<string> expected) => actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static void AssertClean(Census census, Dictionary<string, string> sources) => Offenders(census, sources).Should().BeEmpty(
        "Fix: preserve the separate fixed-origin completion source, owner contracts, prepared target and qualified transition (ADR-0081)");
    private static void Dirty(Census census, Dictionary<string, string> sources, Func<Action> mutation)
    {
        var undo = mutation();
        try { Offenders(census, sources).Should().NotBeEmpty("the planted completion authority violation must fail"); }
        finally { undo(); }
        AssertClean(census, sources);
    }
    private static void DirtySource(Census census, Dictionary<string, string> sources, string path, Func<string, string> mutation)
    {
        var clean = sources[path];
        sources[path] = mutation(clean);
        sources[path].Should().NotBe(clean, "the hostile source control must actually mutate its target");
        try { Offenders(census, sources).Should().NotBeEmpty("the production checker must reject the planted completion violation"); }
        finally { sources[path] = clean; }
        AssertClean(census, sources);
    }
    private static Action RemoveCall(MethodDefinition body, string type, string name)
    {
        var instruction = body.Body.Instructions.Single(item => item.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == name);
        var opcode = instruction.OpCode;
        var operand = instruction.Operand;
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
        return () => { instruction.OpCode = opcode; instruction.Operand = operand; };
    }
    private static Action AddCaller(Census census, MethodReference target)
    {
        var type = census.Type(Source);
        var method = new MethodDefinition("UnownedCompletionControl", MethodAttributes.Private | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, type.Module.ImportReference(target)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return () => type.Methods.Remove(method);
    }
    private sealed record Reference(MethodDefinition Method, MethodReference Target, Instruction Instruction);
    private sealed record Projection(string Metadata, string Properties, string Sql);
}
