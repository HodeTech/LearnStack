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

/// <summary>Checks the actual compiled authority/caller graph and parsed fixed SQL, with reversible hostile controls.</summary>
public sealed class FixedSeedPrerequisiteConventionTests
{
    private const string Witness = "LearnStack.SharedKernel.Permissions.SeedPrerequisiteReadWitness";
    private const string Minter = Witness + "/MintingSource";
    private const string Source = "LearnStack.Tools.Seeder.FixedSeedPrerequisiteSource";
    private const string Verifier = "LearnStack.Tools.Seeder.FixedSeedPrerequisiteVerifier";
    private const string Resolver = "LearnStack.Tools.Seeder.FixedSeedPrerequisiteReadOperationResolver";
    private const string Reader = "LearnStack.Modules.Identity.Infrastructure.Persistence.FixedSeedIdentityPrerequisiteReader";
    private const string Declaration = "LearnStack.Modules.Identity.Application.Contracts.Seeding.FixedSeedPrerequisiteDeclaration";
    private const string Unit = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
    private const string AdapterPort = Unit + "/IFixedSeedRead";
    private const string PrerequisiteAdapter = Unit + "/PrerequisiteSeedRead";
    private const string CompletionAdapter = Unit + "/CompletionSeedRead";
    private const string CompletionWitness = "LearnStack.SharedKernel.Permissions.SeedCompletionReadWitness";
    private const string CompletionOperationPort = "LearnStack.SharedKernel.Persistence.ISeedCompletionReadOperation";
    private const string CompletionResolverPort = "LearnStack.SharedKernel.Persistence.ISeedCompletionReadOperationResolver";
    private const string UnitPort = "LearnStack.SharedKernel.Persistence.IFixedSeedPrerequisiteUnitOfWork";
    private const string OperationPort = "LearnStack.SharedKernel.Persistence.ISeedPrerequisiteReadOperation";
    private const string ResolverPort = "LearnStack.SharedKernel.Persistence.ISeedPrerequisiteReadOperationResolver";
    private const string Enrollment = "LearnStack.SharedKernel.Persistence.IUnitOfWorkContextEnrollment";
    private const string WriteGuard = "LearnStack.SharedKernel.Persistence.IUnitOfWorkWriteGuard";
    private const string SaveGuard = "LearnStack.Infrastructure.Persistence.UnitOfWorkWriteBarrierInterceptor";
    private const string Factory = "LearnStack.Infrastructure.Persistence.ModuleDbContextRegistration";
    private const string CommandGuard = "LearnStack.Infrastructure.Persistence.TenantContextGuardInterceptor";
    private const string SourcePath = "LearnStack.Tools.Seeder/FixedSeedPrerequisiteSource.cs";
    private const string ReaderPath = "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/FixedSeedIdentityPrerequisiteReader.cs";
    private const string OwnerPath = "LearnStack.Infrastructure/Persistence/NpgsqlUnitOfWork.SeedPrerequisites.cs";
    private const string FactoryPath = "LearnStack.Infrastructure/Persistence/ModuleDbContextRegistration.cs";
    private const string CommandGuardPath = "LearnStack.Infrastructure/Persistence/TenantContextGuardInterceptor.cs";
    private const string HumanPath = "LearnStack.SharedKernel/Permissions/OperationReadWitness.cs";
    private const string EnableSql = "SELECT set_config('app.scope', 'tenant', true)";
    private const string ClearSql = "SELECT set_config('app.scope', '', true)";

    private static readonly string[] SourcePaths = [SourcePath, ReaderPath, OwnerPath, FactoryPath, CommandGuardPath, HumanPath];
    private static readonly string[] WitnessParameters = [Witness, "System.Threading.CancellationToken"];
    private static readonly string[] BindingFields = ["_expectedUnit", "_expectedTransaction", "_expectedGeneration", "_binding"];
    private static readonly string[] FixedCommandTypes =
    [
        "LearnStack.Modules.Tenancy.Application.Contracts.Tenant.CreateOrganizationCommand",
        "LearnStack.Modules.Tenancy.Application.Contracts.Tenant.MapHostToTenantCommand",
        "LearnStack.Modules.Tenancy.Application.Contracts.Locales.AddTenantLocaleCommand",
        "LearnStack.Modules.Tenancy.Application.Contracts.Branding.SetTenantBrandingCommand",
        "LearnStack.Modules.Identity.Application.Contracts.Seeding.InitializeSeedRoleCommand",
        "LearnStack.Modules.Identity.Application.Contracts.Seeding.CreateFirstSeedAdminMembershipCommand",
        "LearnStack.Modules.Identity.Application.Contracts.Seeding.CreateSeedMembershipCommand",
        "LearnStack.Modules.Identity.Application.Contracts.Seeding.SetSeedMembershipProfileCommand",
    ];
    private static readonly string[] ComposedTypes = [Source, Verifier, Resolver, Reader];
    private static readonly string[] ExpectedRegistrations =
    [
        "LearnStack.Tools.Seeder.SeedComposition::Build|" + Source,
        "LearnStack.Tools.Seeder.SeedComposition::Build|LearnStack.Modules.Identity.Application.Contracts.Seeding.IFixedSeedPrerequisiteVerifier," + Verifier,
        "LearnStack.Tools.Seeder.SeedComposition::Build|" + ResolverPort + "," + Resolver,
        "LearnStack.Tools.Seeder.SeedComposition::Build|LearnStack.Modules.Identity.Application.Contracts.Seeding.IFixedSeedIdentityPrerequisiteReader," + Reader,
    ];
    private static readonly Dictionary<string, string> FixedConstructors = new(StringComparer.Ordinal)
    {
        ["CreateOrganizationCommand"] = "new CreateOrganizationCommand(tenant.SecondOrganization.OrganizationId, tenant.SecondOrganization.Slug, tenant.SecondOrganization.DisplayName)",
        ["MapHostToTenantCommand"] = "new MapHostToTenantCommand(tenant.Host, tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null, true, true)",
        ["AddTenantLocaleCommand"] = "new AddTenantLocaleCommand(1L + index, locale.Locale, locale.IsEnabled, locale.IsDefault, locale.Sort)",
        ["SetTenantBrandingCommand"] = "new SetTenantBrandingCommand(curriculum.Theme.Id, curriculum.Theme.Value, null)",
        ["InitializeSeedRoleCommand"] = "new InitializeSeedRoleCommand(role.Id, role.BuiltInKey, role.Scope, manifest.Version, manifest.RoleFingerprint(tenant.TenantId, role.Id, role.BuiltInKey, role.Scope))",
        ["CreateFirstSeedAdminMembershipCommand"] = "new CreateFirstSeedAdminMembershipCommand(member.Id, member.UserId, member.RoleId, manifest.Version, fingerprint)",
        ["CreateSeedMembershipCommand"] = "new CreateSeedMembershipCommand(member.Id, member.UserId, member.RoleId, scope, manifest.Version, fingerprint)",
        ["SetSeedMembershipProfileCommand"] = "new SetSeedMembershipProfileCommand(member.Id, member.UserId, member.RoleId, scope, profile.DisplayName, profile.PreferredLocale, manifest.Version, manifest.ProfileFingerprint(tenant.TenantId, member.OrganizationId, member.Id, profile.DisplayName, profile.PreferredLocale))",
    };
    private static readonly Projection[] Projections =
    [
        new("RoleMetadata", "roles", ["id:Id", "tenant_id:TenantId", "built_in_key:BuiltInKey", "permission_scope:Scope",
            "row_version:Version", "created_at:CreatedAt", "created_by:CreatedBy", "updated_at:UpdatedAt", "updated_by:UpdatedBy",
            "deleted_at:DeletedAt", "deleted_by:DeletedBy", "adopted_release_version:ReleaseVersion", "adopted_release_fingerprint:ReleaseFingerprint"],
            "id", "declaration.Roles.Length + 1"),
        new("GrantMetadata", "role_permission_grants", ["role_id:RoleId", "permission_scope:Scope", "permission_key:PermissionKey"],
            "role_id, permission_scope, permission_key", "declaration.Roles.Sum(role => role.Grants.Length) + 1"),
        new("MembershipMetadata", "memberships", ["id:Id", "tenant_id:TenantId", "organization_id:OrganizationId", "user_id:UserId",
            "status:Status", "row_version:Version", "created_at:CreatedAt", "created_by:CreatedBy", "updated_at:UpdatedAt", "updated_by:UpdatedBy",
            "deleted_at:DeletedAt", "deleted_by:DeletedBy"], "id", "declaration.Memberships.Length + 1"),
        new("ProfileMetadata", "membership_profiles", ["membership_id:MembershipId", "organization_id:OrganizationId"],
            "membership_id", "declaration.Memberships.Length + 1"),
        new("AssignmentMetadata", "membership_role_assignments", ["membership_id:MembershipId", "organization_id:OrganizationId", "role_id:RoleId"],
            "membership_id, role_id", "declaration.Memberships.Length + 1"),
    ];
    private static readonly Regex ScopeOnlyStatement = new(@"^\s*(?:SELECT\s+(?:pg_catalog\.)?set_config\s*\(\s*'app\.scope'|SET\s+LOCAL\s+app\.scope\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SelectStatement = new(@"^\s*SELECT\s+(?<columns>.+?)\s+FROM\s+public\.(?<table>[a-z_]+)\s+WHERE\s+tenant_id\s*=\s*@tenant\s+ORDER\s+BY\s+(?<order>[a-z_,\s]+)\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex Column = new("^\\s*(?<column>[a-z_]+)\\s+AS\\s+\"(?<alias>[A-Za-z]+)\"\\s*$", RegexOptions.CultureInvariant);

    [Fact]
    public void Fixed_Seed_Prerequisites_Use_A_Closed_Temporary_Read_Scope()
    {
        using var census = Census.Read();
        var sources = SourcePaths.ToDictionary(path => path,
            path => File.ReadAllText(Path.Combine(SourceScan.SourceRoot, path)), StringComparer.Ordinal);
        AssertClean(census, sources);

        Dirty(census, sources, () =>
        {
            var module = census.Type(Source).Module;
            var extra = new TypeDefinition("SeedAuthorityControl", "UnownedMinter", TypeAttributes.NotPublic,
                module.ImportReference(census.Type(Minter)));
            module.Types.Add(extra);
            return () => module.Types.Remove(extra);
        });
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
            Dirty(census, sources, () => AddMethod(census, opcode, census.Method(UnitPort, "VerifySeedPrerequisitesAsync")));
        Dirty(census, sources, () =>
        {
            var adapter = census.Type(AdapterPort);
            var original = adapter.Attributes;
            adapter.IsNestedPublic = true;
            return () => adapter.Attributes = original;
        });
        Dirty(census, sources, () => AddMethod(census, OpCodes.Ldstr, EnableSql));
        Dirty(census, sources, () => RemoveCall(census.Body(SaveGuard, "SavingChanges"), WriteGuard, "RequireWriteAllowed"));
        Dirty(census, sources, () => RemoveCall(census.Body(Unit, "BeginTransactionCoreAsync"), Unit, "RequireWriteAllowed"));
        Dirty(census, sources, () =>
        {
            var constructor = census.Type(Witness).Methods.Single(method => method.IsConstructor);
            var original = constructor.Attributes;
            constructor.IsPublic = true;
            return () => constructor.Attributes = original;
        });
        Dirty(census, sources, () =>
        {
            var method = census.Method(UnitPort, "VerifySeedPrerequisitesAsync");
            var parameter = new ParameterDefinition("callback", ParameterAttributes.None,
                method.Module.ImportReference(typeof(Action)));
            method.Parameters.Add(parameter);
            return () => method.Parameters.Remove(parameter);
        });
        DirtySource(census, sources, SourcePath, text => text.Replace("ReferenceEquals(policy, invocation.Policy)", "true", StringComparison.Ordinal));
        DirtySource(census, sources, SourcePath, text => text.Replace("new AddTenantLocaleCommand(1L + index,", "new AddTenantLocaleCommand(0L,", StringComparison.Ordinal));
        DirtySource(census, sources, FactoryPath, text => text.Replace(
            ".AddInterceptors(new UnitOfWorkWriteBarrierInterceptor(unitOfWork), new TenantContextGuardInterceptor(unitOfWork))",
            ".AddInterceptors(new TenantContextGuardInterceptor(unitOfWork), new UnitOfWorkWriteBarrierInterceptor(unitOfWork))", StringComparison.Ordinal));
        DirtySource(census, sources, ReaderPath, AddProfileValue);
        var ownerSource = sources[OwnerPath];
        var sanitized = WithoutFixedSeedScopeAnnouncements(OwnerPath, ownerSource);
        sanitized.Should().NotBe(ownerSource);
        ScopeOnlyStatement.IsMatch(EnableSql).Should().BeTrue();
        foreach (var dirty in new[]
        {
            ownerSource.Replace("_depth != 1", "false", StringComparison.Ordinal),
            ownerSource.Replace("VerifySeedPrerequisitesAsync", "UnreviewedVerificationAsync", StringComparison.Ordinal),
            ownerSource.Replace("VerifyFixedSeedReadAsync", "UnreviewedTransitionAsync", StringComparison.Ordinal),
            ownerSource.Replace(ClearSql, EnableSql, StringComparison.Ordinal),
        }) WithoutFixedSeedScopeAnnouncements(OwnerPath, dirty).Should().Be(dirty,
            "a changed owner/signature/gate or SQL pair receives no carrier exemption");
        var extraSite = ownerSource + "\nclass OtherScopeSite { void Run() { Execute(\"SET LOCAL app.scope = 'tenant'\"); } }";
        WithoutFixedSeedScopeAnnouncements(OwnerPath, extraSite).Should().EndWith(
            "class OtherScopeSite { void Run() { Execute(\"SET LOCAL app.scope = 'tenant'\"); } }");
        WithoutFixedSeedScopeAnnouncements("Unlisted.cs", ownerSource).Should().Be(ownerSource);
    }

    /// <summary>Sanitizes only two reviewed literal arguments; all other scope-setting code remains visible.</summary>
    internal static string WithoutFixedSeedScopeAnnouncements(string relative, string source)
    {
        if (relative != OwnerPath) return source;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return source;
        var owners = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type =>
            type.Identifier.ValueText == "NpgsqlUnitOfWork"
            && string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString()))
                == "LearnStack.Infrastructure.Persistence").ToArray();
        if (owners.Length != 1) return source;
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(method =>
            method.Identifier.ValueText == "VerifyFixedSeedReadAsync").ToArray();
        if (methods.Length != 1 || methods[0].Parent != owners[0]
            || !methods[0].Modifiers.Any(SyntaxKind.PrivateKeyword) || !methods[0].Modifiers.Any(SyntaxKind.AsyncKeyword)
            || methods[0].Modifiers.Any(SyntaxKind.StaticKeyword)
            || !SyntaxFactory.AreEquivalent(methods[0].ReturnType, SyntaxFactory.ParseTypeName("Task<bool>"))
            || !SyntaxFactory.AreEquivalent(methods[0].ParameterList, SyntaxFactory.ParseParameterList(
                "(IFixedSeedRead witness, CancellationToken cancellationToken)"))) return source;
        var errors = new List<string>();
        CheckOwner(source, errors);
        if (errors.Count != 0) return source;
        var calls = methods[0].DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is IdentifierNameSyntax { Identifier.ValueText: "ExecuteAsync" }
            && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression) && ScopeOnlyStatement.IsMatch(literal.Token.ValueText)).ToArray();
        if (calls.Length != 2 || !Same(calls.Select(call => ((LiteralExpressionSyntax)call.ArgumentList.Arguments[0].Expression).Token.ValueText),
                [EnableSql, ClearSql])
            || calls.Any(call => call.ArgumentList.Arguments.Count != 2
                || !SyntaxFactory.AreEquivalent(call.ArgumentList.Arguments[1].Expression, SyntaxFactory.ParseExpression("cancellationToken")))) return source;
        foreach (var literal in calls.Select(call => call.ArgumentList.Arguments[0].Expression).OrderByDescending(expression => expression.SpanStart))
            source = source.Remove(literal.SpanStart, literal.Span.Length).Insert(literal.SpanStart, "string.Empty");
        return source;
    }

    private static List<string> Offenders(Census census, Dictionary<string, string> sources)
    {
        var errors = new List<string>();
        var types = census.Types;
        var methods = census.Methods;
        var references = methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => new Reference(method, (MethodReference)instruction.Operand, instruction))).ToArray();
        var witness = census.Type(Witness);
        if (!witness.IsSealed || !witness.IsPublic || witness.Methods.Count(method => method.IsConstructor) != 1
            || !witness.Methods.Single(method => method.IsConstructor).IsPrivate
            || witness.Properties.Any(property => property.SetMethod is not null)
            || witness.Fields.Any(field => !field.IsPrivate || field.IsStatic || (!field.IsInitOnly && field.Name != "_binding")))
            errors.Add("The seed witness has only private construction and immutable source/declaration/expected-owner pins");
        foreach (var name in BindingFields)
            if (!witness.Fields.Any(field => field.Name == name)) errors.Add("Missing seed binding field: " + name);
        ExactTypes(types.Where(type => type.BaseType?.FullName == Minter), [Source], "seed witness minters");
        Implementations(UnitPort, [Unit]);
        Implementations(OperationPort, [Reader]);
        Implementations(ResolverPort, [Resolver]);
        Implementations(Enrollment, [Unit]);
        Implementations("LearnStack.Modules.Identity.Application.Contracts.Seeding.IFixedSeedIdentityPrerequisiteReader", [Reader]);
        Exact(Witness, ".ctor", [Minter + "::ForPrerequisiteRead"]);
        Exact(Minter, "ForPrerequisiteRead", [Source + "::Capture"]);
        Exact(Source, "Capture", [Verifier + "::VerifyAsync"]);
        Exact(Source, "CaptureFixedRequests", [Source + "::.ctor"]);
        Exact(UnitPort, "VerifySeedPrerequisitesAsync", [Verifier + "::VerifyAsync"]);
        Exact(Unit, "VerifySeedPrerequisitesAsync", []);
        Exact(OperationPort, "ExecuteAsync", [PrerequisiteAdapter + "::ExecuteAsync"]);
        Exact(ResolverPort, "Resolve", [PrerequisiteAdapter + "::Resolve"]);
        Exact(Witness, "TryBindTo", [PrerequisiteAdapter + "::TryBindTo"]);
        Exact(Witness, "IsBoundTo", [PrerequisiteAdapter + "::IsBoundTo", Reader + "::RequireCurrent"]);
        Exact(Witness, "RequireCurrent", [PrerequisiteAdapter + "::RequireCurrent", Reader + "::RequireCurrent",
            Verifier + "::VerifyAsync", Verifier + "::VerifyAsync", Verifier + "::VerifyAsync"]);
        Exact(Unit, "VerifyFixedSeedReadAsync", [Unit + "::VerifySeedPrerequisitesAsync", Unit + "::VerifySeedCompletionAsync"]);
        Exact(PrerequisiteAdapter, ".ctor", [Unit + "::VerifySeedPrerequisitesAsync"]);
        Exact(CompletionAdapter, ".ctor", [Unit + "::VerifySeedCompletionAsync"]);
        Exact(AdapterPort, "Resolve", [Unit + "::VerifyFixedSeedReadAsync"]);
        Exact(AdapterPort, "ExecuteAsync", [Unit + "::VerifyFixedSeedReadAsync"]);
        Exact(AdapterPort, "TryBindTo", [Unit + "::VerifyFixedSeedReadAsync"]);
        Exact(AdapterPort, "IsBoundTo", [Unit + "::VerifyFixedSeedReadAsync", Unit + "::ValidateFixedSeedRead"]);
        Exact(AdapterPort, "RequireCurrent", [Unit + "::VerifyFixedSeedReadAsync", Unit + "::VerifyFixedSeedReadAsync",
            Unit + "::RequireCurrentFixedSeedRead"]);
        Implementations(AdapterPort, [PrerequisiteAdapter, CompletionAdapter]);
        if (!census.Type(AdapterPort).IsNestedPrivate || !census.Type(AdapterPort).IsInterface
            || new[] { PrerequisiteAdapter, CompletionAdapter }.Any(type =>
                !census.Type(type).IsNestedPrivate || !census.Type(type).IsSealed)
            || !census.Method(Unit, "VerifyFixedSeedReadAsync").IsPrivate)
            errors.Add("The common transition and its two sealed typed adapters remain private to the physical owner");
        foreach (var (adapter, typedWitness, resolver, operation) in new[]
        {
            (PrerequisiteAdapter, Witness, ResolverPort, OperationPort),
            (CompletionAdapter, CompletionWitness, CompletionResolverPort, CompletionOperationPort),
        })
        {
            var constructor = census.Type(adapter).Methods.Single(method => method.IsConstructor);
            if (!constructor.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual([typedWitness, resolver]))
                errors.Add("The private adapter accepts only its exact typed witness and registered resolver: " + adapter);
            Require(adapter, "Resolve", resolver, "Resolve", 1);
            Require(adapter, "ExecuteAsync", operation, "ExecuteAsync", 1);
            Require(adapter, "TryBindTo", typedWitness, "TryBindTo", 1);
            Require(adapter, "IsBoundTo", typedWitness, "IsBoundTo", 1);
            Require(adapter, "RequireCurrent", typedWitness, "RequireCurrent", 1);
        }
        Exact(Witness, "GetDeclaration", [Source + "::IsCurrent", Reader + "::ReadAsync", Verifier + "::VerifyAsync"]);
        Exact(Reader, "Query", Enumerable.Repeat(Reader + "::ReadAsync", Projections.Length).ToArray());
        Exact("LearnStack.Modules.Identity.Application.Contracts.Seeding.IFixedSeedIdentityPrerequisiteReader", "VerifyAsync", []);
        Exact(Reader, "VerifyAsync", [Reader + "::ExecuteAsync"]);
        Exact(Enrollment, "RegisterModuleContext", [Factory + "::AddModuleDbContext"]);
        Exact(Enrollment, "RequireModuleCommandAllowed", [CommandGuard + "::Guard"]);
        if (!Same(Calls(census.Body(Source, "CaptureFixedRequests")).Where(call => call.Name == ".ctor"
                    && call.DeclaringType.Name.EndsWith("Command", StringComparison.Ordinal)).Select(call => call.DeclaringType.FullName), FixedCommandTypes))
            errors.Add("The eight constructed request names bind the actual owning contract types");
        var registrations = references.Where(reference => reference.Target is GenericInstanceMethod generic
                && reference.Target.Name == "AddScoped" && generic.GenericArguments.Any(argument => ComposedTypes.Contains(argument.FullName, StringComparer.Ordinal)))
            .Select(reference => Owner(census, reference.Method) + "|" + string.Join(",", ((GenericInstanceMethod)reference.Target).GenericArguments.Select(argument => argument.FullName)));
        if (!Same(registrations, ExpectedRegistrations)) errors.Add("Only the Seeder root composes the one source, bridge, resolver and metadata reader");
        foreach (var record in new[] { Declaration, "LearnStack.Modules.Identity.Application.Contracts.Seeding.FixedSeedRoleDeclaration",
                     "LearnStack.Modules.Identity.Application.Contracts.Seeding.FixedSeedMembershipDeclaration", Source + "/FixedRequest" })
        {
            var carrier = census.Type(record);
            if (!carrier.IsSealed || carrier.Fields.Any(field => !field.IsPrivate || !field.IsInitOnly || field.IsStatic))
                errors.Add("The captured declaration remains an immutable sealed record: " + record);
        }
        foreach (var (type, property) in new[] { (Declaration, "Roles"), (Declaration, "Memberships"),
                     ("LearnStack.Modules.Identity.Application.Contracts.Seeding.FixedSeedRoleDeclaration", "Grants") })
            if (census.Type(type).Properties.Single(item => item.Name == property).PropertyType.GetElementType().FullName
                != "System.Collections.Immutable.ImmutableArray`1")
                errors.Add("The declaration never retains a mutable collection: " + type + "::" + property);
        foreach (var reference in references.Where(reference => reference.Target.DeclaringType.FullName == Witness && reference.Target.Name == "GetDeclaration"))
            if (reference.Target is not GenericInstanceMethod { GenericArguments.Count: 1 } generic
                || generic.GenericArguments[0].FullName != Declaration)
                errors.Add("The declaration cannot escape through an untyped or alternate projection");

        Signature(UnitPort, "VerifySeedPrerequisitesAsync", WitnessParameters, "System.Threading.Tasks.Task`1<System.Boolean>");
        Signature(UnitPort, "VerifySeedCompletionAsync", [CompletionWitness, "System.Threading.CancellationToken"],
            "System.Threading.Tasks.Task`1<System.Boolean>");
        Signature(OperationPort, "ExecuteAsync", WitnessParameters, "System.Threading.Tasks.Task`1<System.Boolean>");
        Signature(ResolverPort, "Resolve", [], OperationPort);
        foreach (var (contract, method) in new[] { (UnitPort, "VerifySeedPrerequisitesAsync"), (OperationPort, "ExecuteAsync"), (ResolverPort, "Resolve") })
            if (census.Type(contract).Methods.Count != (contract == UnitPort ? 2 : 1) || census.Type(contract).Properties.Count != 0
                || census.Type(contract).Events.Count != 0)
                errors.Add("The closed operation port exposes only its named method: " + contract + "::" + method);

        Require(Witness, "get_IsActive", Minter, "get_IsActive", 1);
        Require(Witness, "get_IsActive", Minter, "MatchesCurrent", 1);
        Require(Witness, "get_IsActive", "LearnStack.SharedKernel.Permissions.OperationInvocation", "get_IsActive", 1);
        Require(Minter, "Dispose", "System.Threading.Interlocked", "Exchange", 1);
        Require(Witness, "RequireCurrent", "LearnStack.SharedKernel.Identity.IIdentityMutationLocks", "RequireCurrent", 1);
        Require(Source, "Capture", "LearnStack.SharedKernel.Identity.IIdentityMutationLocks", "RequireCurrent", 1);
        Require(Source, "HasInvocation", "LearnStack.Tools.Seeder.SeedOperationSource", "Owns", 1);
        Require(Unit, "VerifyFixedSeedReadAsync", Unit, "RequireCurrentFixedSeedRead", 4);
        Require(Unit, "VerifyFixedSeedReadAsync", Unit, "RequireCleanSeedContexts", 4);
        Require(Unit, "VerifyFixedSeedReadAsync", Unit, "RollbackCoreAsync", 1);
        Require(Unit, "RequireCleanSeedContexts", "Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker", "DetectChanges", 1);
        foreach (var method in new[] { "SavingChanges", "SavingChangesAsync" }) Require(SaveGuard, method, WriteGuard, "RequireWriteAllowed", 1);
        foreach (var method in new[] { "BeginTransactionCoreAsync", "BeginGlobalIdentityTransactionAsync", "AcquireGlobalIdentitySubjectLockAsync",
                     "EnrollGlobalIdentityContext", "ValidateGlobalIdentityConstraintsAsync", "SetTenantContextCoreAsync",
                     "SetProvisioningTenantContextAsync", "BeginSelfDiscoveryTransactionAsync" })
            Require(Unit, method, Unit, "RequireWriteAllowed", 1);
        Require(Unit, "CommitFrameAsync", Unit, "PoisonFixedSeedRead", 1);
        Require(Unit, "ValidateOperationReadScope", Unit, "ValidateFixedSeedRead", 1);
        foreach (var (type, method, count) in new[]
        {
            ("LearnStack.Infrastructure.Audit.PostgresAuditStore", "WritePendingAsync", 2),
            ("LearnStack.Infrastructure.Audit.PostgresAuditStore", "WriteAuthorizationPendingAsync", 2),
            ("LearnStack.Infrastructure.Audit.PostgresAuditStore", "WriteGlobalIdentityPendingAsync", 1),
            ("LearnStack.Infrastructure.Messaging.PostgresOutbox", "EnqueueAsync", 2),
            ("LearnStack.Modules.Identity.Infrastructure.Persistence.ContextualIdentityStorageControl", "LockRootAsync", 1),
            ("LearnStack.Modules.Identity.Infrastructure.Persistence.ContextualIdentityStorageControl", "SaveAsync", 1),
            ("LearnStack.Modules.Identity.Infrastructure.Persistence.ContextualIdentityStorageControl", "ValidateGraphAsync", 1),
        }) Require(type, method, WriteGuard, "RequireWriteAllowed", count);

        var bind = census.Body(Witness, "TryBindTo");
        foreach (var name in BindingFields)
            if (!bind.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Ldfld
                    && instruction.Operand is FieldReference field && field.DeclaringType.FullName == Witness && field.Name == name))
                errors.Add("Binding must check the captured exact owner and prior consumption: " + name);
        var bindingStores = methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Stfld
                && instruction.Operand is FieldReference { DeclaringType.FullName: Witness, Name: "_binding" }).Select(_ => method)).ToArray();
        if (bindingStores.Length != 1 || bindingStores[0] != bind
            || !Calls(bind).Any(call => call.DeclaringType.FullName == "System.Threading.Monitor" && call.Name == "Enter")
            || !Calls(bind).Any(call => call.DeclaringType.FullName == "System.Threading.Monitor" && call.Name == "Exit"))
            errors.Add("One-use binding is stored only under its private monitor");
        foreach (var (type, method) in new[] { (Witness, "get_IsActive"), (Source, "IsCurrent"), (Source, "HasInvocation"),
                     (Source, "Matches"), (Unit, "ValidateFixedSeedRead"), (PrerequisiteAdapter, "IsBoundTo"),
                     (CompletionAdapter, "IsBoundTo") })
            if (Calls(census.Body(type, method)).Any(call => call.DeclaringType.FullName is "LearnStack.SharedKernel.Persistence.IUnitOfWork"
                    or "LearnStack.SharedKernel.Identity.IIdentityMutationLocks"))
                errors.Add("Scope/lifetime getters remain pure and cannot recurse through unit/lock getters: " + type + "::" + method);

        var scopeStatements = methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldstr
                && instruction.Operand is string sql && ScopeOnlyStatement.IsMatch(sql))
            .Select(instruction => (Owner: Owner(census, method), Sql: (string)instruction.Operand))).ToArray();
        if (!Same(scopeStatements.Select(item => item.Owner + "|" + item.Sql),
                [Unit + "::VerifyFixedSeedReadAsync|" + EnableSql, Unit + "::VerifyFixedSeedReadAsync|" + ClearSql]))
            errors.Add("Exactly two scope-only SQL sites belong to the existing physical verification owner");
        var verifyBody = census.Body(Unit, "VerifyFixedSeedReadAsync");
        if (Calls(verifyBody).Any(call => call.DeclaringType.FullName == Unit && call.Name is "ClearTenantContextAnnouncement"
                or "SetTenantContextAsync" or "SetProvisioningTenantContextAsync" or "BeginTransactionAsync")
            || verifyBody.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                && field.DeclaringType.FullName == Unit && field.Name is "_generation" or "_tenantContextAnnouncement" or "_tenantWideRead"))
            errors.Add("Verification never retargets the announcement/generation or borrows the human read state");

        // This new source cannot generalize or inherit the existing Protected-human minter.
        ExactTypes(types.Where(type => type.BaseType?.FullName == "LearnStack.SharedKernel.Permissions.OperationReadWitness/MintingSource"),
            ["LearnStack.SharedKernel.Permissions.ProtectedOperationDecision/MintingSource"], "human read minters");
        if (!HasExpression(Parse(sources[HumanPath]), "invocation.Policy.Kind != OperationPolicyKind.Protected"))
            errors.Add("The human read witness remains Protected-only");

        CheckSource(sources[SourcePath], errors);
        CheckOwner(sources[OwnerPath], errors);
        CheckFactory(sources[FactoryPath], errors);
        if (!HasExpression(Parse(sources[CommandGuardPath]),
                "enrollment.RequireModuleCommandAllowed(context, command.Transaction, isWrite: source is not (CommandSource.LinqQuery or CommandSource.FromSqlQuery))"))
            errors.Add("The EF command guard admits only query command sources during the valid verification phase");
        CheckReader(census, sources[ReaderPath], errors);
        var rawReaderQueries = references.Where(reference =>
                (reference.Method.DeclaringType.FullName == Reader || reference.Method.DeclaringType.FullName.StartsWith(Reader + "/", StringComparison.Ordinal))
                && reference.Target.Name is "SqlQueryRaw" or "SqlQuery" or "FromSqlRaw" or "FromSqlInterpolated").ToArray();
        if (rawReaderQueries.Length != 1 || rawReaderQueries[0].Target.Name != "SqlQueryRaw"
            || Owner(census, rawReaderQueries[0].Method) != Reader + "::Query")
            errors.Add("The reader issues SQL only through its one private literal-fed projection helper");
        return errors;

        void ExactTypes(IEnumerable<TypeDefinition> actual, string[] expected, string description)
        {
            if (!Same(actual.Select(type => type.FullName), expected)) errors.Add("The exact " + description + " changed");
        }
        void Implementations(string contract, string[] expected) => ExactTypes(types.Where(type =>
            type.Interfaces.Any(item => item.InterfaceType.FullName == contract)), expected, contract + " implementations");
        void Exact(string type, string method, string[] expected)
        {
            var matching = references.Where(reference => reference.Target.DeclaringType.FullName == type && reference.Target.Name == method).ToArray();
            if (matching.Any(reference => reference.Instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj))
                || !Same(matching.Select(reference => Owner(census, reference.Method)), expected))
                errors.Add("The closed caller/alias set changed: " + type + "::" + method);
        }
        void Require(string type, string method, string targetType, string targetMethod, int count)
        {
            if (Calls(census.Body(type, method)).Count(call => call.DeclaringType.FullName == targetType && call.Name == targetMethod) != count)
                errors.Add("Missing executing owner barrier: " + type + "::" + method + " -> " + targetType + "::" + targetMethod);
        }
        void Signature(string type, string method, string[] parameters, string result)
        {
            var candidate = census.Method(type, method);
            if (candidate.ReturnType.FullName != result || !candidate.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                errors.Add("The closed operation signature changed: " + type + "::" + method);
        }
    }

    private static void CheckSource(string text, List<string> errors)
    {
        var root = Parse(text);
        var capture = Method(root, "CaptureFixedRequests");
        var actual = capture.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(creation => creation.Type.ToString().EndsWith("Command", StringComparison.Ordinal)).ToArray();
        if (!Same(actual.Select(creation => creation.Type.ToString()), FixedConstructors.Keys)
            || actual.Any(creation => !FixedConstructors.TryGetValue(creation.Type.ToString(), out var expected)
                || !SyntaxFactory.AreEquivalent(creation, SyntaxFactory.ParseExpression(expected))))
            errors.Add("Exactly eight independently constructed requests retain their complete fixed values");
        foreach (var expression in new[]
        {
            "ReferenceEquals(invocation!.Operation, request)", "ReferenceEquals(policy, invocation.Policy)",
            "_seedSource.Owns(source, invocation.Operation.GetType(), _context)",
            "pin.Request.GetType() == invocation.Operation.GetType()", "pin.Request.Equals(invocation.Operation)",
            "plan.UserIds.SequenceEqual(pin.Users)", "plan.TenantMode == IdentityTenantLockMode.Exclusive",
            "ReferenceEquals(witness.Invocation, _invocation)", "ReferenceEquals(witness.Plan, _plan)",
            "ReferenceEquals(witness.GetDeclaration<FixedSeedPrerequisiteDeclaration>(), captured.Declaration)",
            "IdentityFoundationManifest.Version1", "requests.ToImmutable()",
        })
            if (!HasExpression(root, expression)) errors.Add("Missing canonical source binding: " + expression);
        var fields = root.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(field =>
            field.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "_requests")).ToArray();
        if (fields.Length != 1 || !fields[0].Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
            || fields[0].Declaration.Type.ToString() != "ImmutableArray<FixedRequest>"
            || !capture.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => Name(call) == "ToImmutableArray"))
            errors.Add("The fixed source snapshots its request/declaration vectors immutably");
    }

    private static void CheckOwner(string text, List<string> errors)
    {
        var root = Parse(text);
        var entryPoints = new[]
        {
            (Method: "VerifySeedPrerequisitesAsync", Witness: "SeedPrerequisiteReadWitness", Adapter: "PrerequisiteSeedRead", Resolver: "_seedPrerequisiteReads"),
            (Method: "VerifySeedCompletionAsync", Witness: "SeedCompletionReadWitness", Adapter: "CompletionSeedRead", Resolver: "_seedCompletionReads"),
        };
        foreach (var entry in entryPoints)
        {
            var entries = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(candidate => candidate.Identifier.ValueText == entry.Method).ToArray();
            if (entries.Length != 1 || !entries[0].Modifiers.Any(SyntaxKind.PublicKeyword)
                || !SyntaxFactory.AreEquivalent(entries[0].ParameterList, SyntaxFactory.ParseParameterList(
                    $"({entry.Witness} witness, CancellationToken cancellationToken = default)"))
                || entries[0].ExpressionBody is not { } body || !SyntaxFactory.AreEquivalent(body.Expression,
                    SyntaxFactory.ParseExpression($"VerifyFixedSeedReadAsync(new {entry.Adapter}(witness, {entry.Resolver}), cancellationToken)")))
                errors.Add("Each public seed entry constructs only its private purpose-specific adapter: " + entry.Method);
        }
        var method = Method(root, "VerifyFixedSeedReadAsync");
        var block = method.Body!.Statements.OfType<TryStatementSyntax>().Single().Block;
        var markers = new SyntaxNode[]
        {
            SyntaxFactory.ParseStatement("_seedReadState = SeedReadState.Enabling;"),
            SyntaxFactory.ParseStatement("await ExecuteAsync(\"" + EnableSql + "\", cancellationToken);"),
            SyntaxFactory.ParseStatement("_seedReadState = SeedReadState.Verifying;"),
            SyntaxFactory.ParseStatement("var admitted = await witness.ExecuteAsync(cancellationToken);"),
            SyntaxFactory.ParseStatement("_seedReadState = SeedReadState.Clearing;"),
            SyntaxFactory.ParseStatement("await ExecuteAsync(\"" + ClearSql + "\", cancellationToken);"),
            SyntaxFactory.ParseStatement("_seedReadState = SeedReadState.Narrow;"),
            SyntaxFactory.ParseStatement("return admitted;"),
        };
        var positions = markers.Select(marker => block.Statements.Select((statement, index) => (statement, index))
            .Where(item => SyntaxFactory.AreEquivalent(item.statement, marker)).Select(item => item.index).ToArray()).ToArray();
        if (positions.Any(position => position.Length != 1)
            || !positions.Select(position => position.SingleOrDefault()).SequenceEqual(positions.Select(position => position.SingleOrDefault()).Order()))
            errors.Add("The common operation enables, reads, clears and restores narrow scope before publishing evidence");
        var returnStatement = block.Statements.OfType<ReturnStatementSyntax>().SingleOrDefault();
        var lastRequire = block.Statements.OfType<ExpressionStatementSyntax>().LastOrDefault(statement =>
            SyntaxFactory.AreEquivalent(statement.Expression, SyntaxFactory.ParseExpression("witness.RequireCurrent()")));
        if (returnStatement is null || lastRequire is null || lastRequire.SpanStart > returnStatement.SpanStart
            || !HasExpression(method, "ReferenceEquals(_tenantContextAnnouncement, announced)"))
            errors.Add("Acknowledged restoration still checks the same announcement and live source/plan");
        foreach (var expression in new[] { "_depth != 1", "_mode != TransactionMode.ReadWrite", "_tenantWideRead", "_readWitnesses.Count != 0",
                     "announced.Generation != _generation", "_seedReadWitness is not null", "_seedReadState != SeedReadState.Narrow", "!witness.IsConfigured" })
            if (!HasExpression(method, expression)) errors.Add("Missing physical-owner admission: " + expression);
        var resolution = block.Statements.OfType<IfStatementSyntax>().SingleOrDefault(statement => HasExpression(statement, "witness.Resolve()"));
        var enabling = block.Statements.SingleOrDefault(statement => SyntaxFactory.AreEquivalent(statement,
            SyntaxFactory.ParseStatement("_seedReadState = SeedReadState.Enabling;")));
        if (resolution is null || enabling is null || !block.Statements.Any(statement => statement.SpanStart > resolution.SpanStart
                && statement.SpanStart < enabling.SpanStart && SyntaxFactory.AreEquivalent(statement,
                    SyntaxFactory.ParseStatement("RequireCleanSeedContexts();"))))
            errors.Add("Contexts constructed by the registered operation resolver are clean before enabling scope");
        if (!Method(root, "RequireWriteAllowed").DescendantNodes().OfType<IfStatementSyntax>().Any(statement =>
                SyntaxFactory.AreEquivalent(statement, SyntaxFactory.ParseStatement("if (_seedReadState != SeedReadState.Narrow) RefuseFixedSeedRead();")))
            || !SyntaxFactory.AreEquivalent(Method(root, "PoisonFixedSeedRead").Body, SyntaxFactory.ParseStatement(
                "{ _seedReadState = SeedReadState.Faulted; _rollbackOnly = true; _tenantContextIssued = false; _tenantContextAnnouncement = null; }")))
            errors.Add("Active write refusal irreversibly poisons the physical owner and removes its usable announcement");
        var clean = Method(root, "RequireCleanSeedContexts");
        if (!clean.DescendantNodes().OfType<CatchClauseSyntax>().Any(handler => handler.Declaration?.Type.ToString() == "ObjectDisposedException"
                && handler.Block.Statements.OfType<IfStatementSyntax>().Any(statement => SyntaxFactory.AreEquivalent(statement,
                    SyntaxFactory.ParseStatement("if (generation == _generation) RefuseFixedSeedRead();")))))
            errors.Add("Disposal cannot hide pending changes on an enrolled current-generation context");
    }

    private static void CheckFactory(string text, List<string> errors)
    {
        var method = Method(Parse(text), "AddModuleDbContext");
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => Name(call) == "AddInterceptors").ToArray();
        var guarded = calls.Where(call => call.ArgumentList.Arguments.Any(argument => argument.Expression is ObjectCreationExpressionSyntax
        { Type: IdentifierNameSyntax { Identifier.ValueText: "UnitOfWorkWriteBarrierInterceptor" } })).ToArray();
        if (calls.Length != 2 || guarded.Length != 1 || !SyntaxFactory.AreEquivalent(guarded[0].ArgumentList,
                SyntaxFactory.ParseArgumentList("(new UnitOfWorkWriteBarrierInterceptor(unitOfWork), new TenantContextGuardInterceptor(unitOfWork))"))
            || calls.Count(call => SyntaxFactory.AreEquivalent(call.ArgumentList, SyntaxFactory.ParseArgumentList("(saveInterceptors)"))) != 1
            || ((MemberAccessExpressionSyntax)calls.Single(call => SyntaxFactory.AreEquivalent(call.ArgumentList,
                    SyntaxFactory.ParseArgumentList("(saveInterceptors)"))).Expression).Name.SpanStart
                < ((MemberAccessExpressionSyntax)guarded[0].Expression).Name.SpanStart
            || !HasExpression(method, "enrollment.RegisterModuleContext(context)"))
            errors.Add("The common factory installs its first flush barrier before capture and enrolls the actual context");
    }

    private static void CheckReader(Census census, string text, List<string> errors)
    {
        var root = Parse(text);
        var reader = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "FixedSeedIdentityPrerequisiteReader");
        var read = Method(root, "ReadAsync");
        var queries = read.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression is GenericNameSyntax
        { Identifier.ValueText: "Query" }).ToArray();
        if (queries.Length != Projections.Length) errors.Add("The closed reader has exactly five literal fixed metadata statements");
        foreach (var projection in Projections)
        {
            var matching = queries.Where(query => ((GenericNameSyntax)query.Expression).TypeArgumentList.Arguments.Single().ToString() == projection.Metadata).ToArray();
            if (matching.Length != 1) { errors.Add("Missing fixed metadata query: " + projection.Metadata); continue; }
            var query = matching[0];
            var arguments = query.ArgumentList.Arguments;
            if (arguments.Count != 2 || arguments[0].Expression is not LiteralExpressionSyntax literal
                || !literal.IsKind(SyntaxKind.StringLiteralExpression) || !SqlMatches(literal.Token.ValueText, projection)
                || !SyntaxFactory.AreEquivalent(arguments[1].Expression, SyntaxFactory.ParseExpression("witness")))
                errors.Add("The fixed query selects only its finite metadata under the exact Tenant predicate: " + projection.Table);
            if (query.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Take" } takeMember
                || takeMember.Parent is not InvocationExpressionSyntax take || take.ArgumentList.Arguments.Count != 1
                || !SyntaxFactory.AreEquivalent(take.ArgumentList.Arguments[0].Expression, SyntaxFactory.ParseExpression(projection.Limit))
                || take.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToListAsync" } listMember
                || listMember.Parent is not InvocationExpressionSyntax list || !SyntaxFactory.AreEquivalent(list.ArgumentList, SyntaxFactory.ParseArgumentList("(ct)")))
                errors.Add("The fixed projection remains independently bounded and uses the caller deadline: " + projection.Table);
            var carrier = reader.Members.OfType<ClassDeclarationSyntax>().SingleOrDefault(type => type.Identifier.ValueText == projection.Metadata);
            if (carrier is null || !carrier.Modifiers.Any(SyntaxKind.PrivateKeyword) || !carrier.Modifiers.Any(SyntaxKind.SealedKeyword)
                || !Same(carrier.Members.OfType<PropertyDeclarationSyntax>().Select(property => property.Identifier.ValueText), projection.Columns.Select(column => column.Split(':')[1])))
                errors.Add("The private metadata carrier cannot grow profile/provider fields: " + projection.Metadata);
        }
        var queryHelper = Method(root, "Query");
        if (!queryHelper.Modifiers.Any(SyntaxKind.PrivateKeyword) || queryHelper.ExpressionBody is not { } body
            || !SyntaxFactory.AreEquivalent(body.Expression, SyntaxFactory.ParseExpression(
                "db.Database.SqlQueryRaw<T>(sql, new NpgsqlParameter(\"tenant\", witness.TenantId.Value))")))
            errors.Add("All submitted SQL is confined to the five literal callers and one fixed parameterized helper");
        var localQueries = reader.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => Name(call) == "Query").ToArray();
        if (localQueries.Length != Projections.Length) errors.Add("No additional caller can submit SQL to the fixed metadata helper");
        var unsafeCalls = census.Methods.Where(method => method.DeclaringType.FullName == Reader
                || method.DeclaringType.FullName.StartsWith(Reader + "/", StringComparison.Ordinal))
            .SelectMany(Calls).Where(call => call.Name is "CreateCommand" or "ExecuteNonQuery" or "ExecuteNonQueryAsync" or "ExecuteScalar" or "ExecuteScalarAsync"
                or "IgnoreQueryFilters" or "ExecuteSqlRawAsync" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync"
                || call.DeclaringType.FullName.StartsWith("Microsoft.Extensions.Logging.", StringComparison.Ordinal)
                || call.DeclaringType.FullName.StartsWith("LearnStack.SharedKernel.Caching.", StringComparison.Ordinal)).ToArray();
        if (unsafeCalls.Length != 0) errors.Add("The metadata reader cannot issue alternate SQL, bypass filters or publish personal evidence");
    }

    private static bool SqlMatches(string sql, Projection projection)
    {
        var match = SelectStatement.Match(sql);
        if (!match.Success || match.Groups["table"].Value != projection.Table
            || Regex.Replace(match.Groups["order"].Value.Trim(), @"\s+", " ") != projection.Order) return false;
        var columns = match.Groups["columns"].Value.Split(',').Select(value => Column.Match(value)).ToArray();
        return columns.All(column => column.Success) && columns.Select(column =>
                column.Groups["column"].Value + ":" + column.Groups["alias"].Value).SequenceEqual(projection.Columns, StringComparer.Ordinal);
    }

    private static string AddProfileValue(string text)
    {
        var root = Parse(text);
        var query = Method(root, "ReadAsync").DescendantNodes().OfType<InvocationExpressionSyntax>().Single(call =>
            call.Expression is GenericNameSyntax { Identifier.ValueText: "Query" } name
            && name.TypeArgumentList.Arguments.Single().ToString() == "ProfileMetadata");
        var literal = (LiteralExpressionSyntax)query.ArgumentList.Arguments[0].Expression;
        var expanded = literal.Token.ValueText.Replace("SELECT membership_id", "SELECT display_name AS \"DisplayName\", membership_id", StringComparison.Ordinal);
        return root.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(expanded))).ToFullString();
    }

    private static SyntaxNode Parse(string text)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        root.ContainsDiagnostics.Should().BeFalse("the structural rule must inspect valid C# syntax");
        return root;
    }
    private static MethodDeclarationSyntax Method(SyntaxNode root, string name) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name);
    private static bool HasExpression(SyntaxNode root, string text) => root.DescendantNodesAndSelf().OfType<ExpressionSyntax>()
        .Any(expression => SyntaxFactory.AreEquivalent(expression, SyntaxFactory.ParseExpression(text)));
    private static string? Name(InvocationExpressionSyntax call) => call.Expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        GenericNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };
    private static string Owner(Census census, MethodDefinition method)
    {
        if (method.DeclaringType.FullName.StartsWith(Factory + "/", StringComparison.Ordinal)
            && method.Name.StartsWith("<AddModuleDbContext>", StringComparison.Ordinal)) return Factory + "::AddModuleDbContext";
        return census.Owner(method);
    }
    private static IEnumerable<MethodReference> Calls(MethodDefinition method) => method.Body.Instructions
        .Where(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj)
        .Select(instruction => instruction.Operand).OfType<MethodReference>();
    private static bool Same(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static void AssertClean(Census census, Dictionary<string, string> sources) =>
        Offenders(census, sources).Should().BeEmpty("Fix: preserve the actual fixed-source, reader, owner transition and write barriers (ADR-0080)");
    private static void Dirty(Census census, Dictionary<string, string> sources, Func<Action> mutation)
    {
        var undo = mutation();
        try { Offenders(census, sources).Should().NotBeEmpty("the planted actual authority/caller/barrier violation must fail"); }
        finally { undo(); }
        AssertClean(census, sources);
    }
    private static void DirtySource(Census census, Dictionary<string, string> sources, string path, Func<string, string> mutation)
    {
        var clean = sources[path];
        var dirty = mutation(clean);
        dirty.Should().NotBe(clean, "the source control must actually change its target");
        sources[path] = dirty;
        try { Offenders(census, sources).Should().NotBeEmpty("the same parsed production validator must reject the planted source violation"); }
        finally { sources[path] = clean; }
        AssertClean(census, sources);
    }
    private static Action RemoveCall(MethodDefinition method, string type, string name)
    {
        var instruction = method.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == type && target.Name == name);
        var opcode = instruction.OpCode;
        var operand = instruction.Operand;
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
        return () => { instruction.OpCode = opcode; instruction.Operand = operand; };
    }
    private static Action AddMethod(Census census, OpCode opcode, object operand)
    {
        var type = census.Type(Source);
        var method = new MethodDefinition("UnownedSeedAuthority", MethodAttributes.Private | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(operand is MethodReference target
            ? Instruction.Create(opcode, type.Module.ImportReference(target)) : Instruction.Create(opcode, (string)operand));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return () => type.Methods.Remove(method);
    }

    private sealed record Projection(string Metadata, string Table, string[] Columns, string Order, string Limit);
    private sealed record Reference(MethodDefinition Method, MethodReference Target, Instruction Instruction);
}
