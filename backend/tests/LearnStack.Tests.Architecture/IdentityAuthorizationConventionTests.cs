using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0076 Step 2's compiled candidate/reader/source boundaries; later policy/evidence obligations remain Registered.</summary>
public sealed class IdentityAuthorizationConventionTests
{
    private const string Candidate = "LearnStack.SharedKernel.Permissions.AuthorizationCandidate";
    private const string Minter = Candidate + "/MintingSource";
    private const string HttpMinter = "LearnStack.Api.Authentication.TenantAuthorizationCandidateSource";
    private const string Source = "LearnStack.Infrastructure.Persistence.IdentityAuthorizationReadSource";
    private const string Owner = "LearnStack.Infrastructure.Persistence.AuthorizationReadOwner";
    private const string RoleGuard = "LearnStack.Infrastructure.Persistence.ApplicationDataSource";
    private const string Reader = "LearnStack.Modules.Identity.Infrastructure.Authorization.PreContextIdentityAuthorizationSessionFactory";
    private const string Orchestrator = "LearnStack.Modules.Identity.Application.Authorization.CommittedIdentityAuthorizationReader";
    private const string Session = Reader + "/AuthorizationSession";
    private const string SessionFactoryPort = "LearnStack.Modules.Identity.Application.Abstractions.IIdentityAuthorizationSessionFactory";
    private const string SessionPort = "LearnStack.Modules.Identity.Application.Abstractions.IIdentityAuthorizationSession";
    private const string ReaderPort = "LearnStack.Modules.Identity.Application.Contracts.Authorization.IIdentityAuthorizationReader";
    private const string Lifecycle = "LearnStack.Modules.Tenancy.Infrastructure.Persistence.IdentityScopeLifecycleReader";
    private const string LifecyclePort = "LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle.IIdentityScopeLifecycleReader";
    private const string NavigationReaderPort = "LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle.IOwnTenantNavigationReader";
    private const string NavigationCoveragePort = "LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle.IOwnTenantNavigationCoverage";
    private const string NavigationAccess = "LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle.OwnTenantNavigationAccess";
    private const string NavigationReader = "LearnStack.Modules.Tenancy.Infrastructure.Persistence.OwnTenantNavigationReader";
    private const string NavigationCoverage = "LearnStack.Modules.Identity.Application.Memberships.OwnTenantNavigationCoverage";
    private const string AdministrationReadAuthorization = "LearnStack.Modules.Identity.Application.Authorization.IdentityAdministrationReadAuthorization";
    private const string AdministrationReadStorePort = "LearnStack.Modules.Identity.Application.Abstractions.IIdentityAdministrationReadStore";
    private const string AdministrationReadStore = "LearnStack.Modules.Identity.Infrastructure.Persistence.IdentityAdministrationReadStore";


    [Fact]
    public void Authorization_candidates_have_one_verified_minter_and_closed_lifetime_evidence()
    {
        using var census = Census.Read();
        CandidateOffenders(census).Should().BeEmpty("Fix: preserve the private candidate factory and the exact verified HTTP source; a UserId alone grants nothing (ADR-0076)");
        var candidate = census.Type(Candidate);
        var constructor = candidate.Methods.Single(method => method.IsConstructor);
        var original = constructor.Attributes;
        Dirty(census, CandidateOffenders, () =>
        {
            constructor.Attributes = (original & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
            return () => constructor.Attributes = original;
        });
        Dirty(census, CandidateOffenders, () =>
        {
            var module = census.Type(HttpMinter).Module;
            var extra = new TypeDefinition("AuthorizationControl", "AdditionalMinter", TypeAttributes.NotPublic,
                module.ImportReference(census.Type(Minter)));
            module.Types.Add(extra);
            return () => module.Types.Remove(extra);
        });
        Dirty(census, CandidateOffenders, () => RemoveCall(census.Body(Candidate, "get_IsActive"), Minter, "MatchesEvidence"));
        Dirty(census, CandidateOffenders, () => RemoveCall(census.Body(Minter, "Dispose"), "System.Threading.Interlocked", "Exchange"));
        Dirty(census, CandidateOffenders, () => RemoveInstruction(EvidenceComparisons(census.Body(HttpMinter, "IsEvidenceCurrent")).First()));
        Dirty(census, CandidateOffenders, () =>
        {
            var body = census.Body(HttpMinter, "IsEvidenceCurrent");
            var comparison = EvidenceComparisons(body).First();
            var branch = IsCall(comparison, "System.Object", "ReferenceEquals") ? comparison.Next : comparison;
            return Replace(branch, branch.OpCode, body.Body.Instructions.First(instruction => instruction.OpCode == OpCodes.Ret));
        });
        Dirty(census, CandidateOffenders, () => RemoveCall(census.Body(HttpMinter, "ForValidatedScope"),
            "LearnStack.Api.Authentication.TenantSubjectAdmissionProvenance", "Matches"));
    }

    [Fact]
    public void Authorization_borrowers_and_lifecycle_callers_are_exact_and_publish_no_raw_source_or_callback()
    {
        using var census = Census.Read();
        BorrowerOffenders(census).Should().BeEmpty("Fix: borrow only through the enumerated candidate reader and keep its datasource private (ADR-0076)");
        AssertProviderTenancyBoundaryControls(census);
        AssertOwnNavigationControls(census);
        foreach (var target in new[] { census.Method(Source, "OpenAsync"), census.Method(LifecyclePort, "ReadAuthorizationAsync"),
                     census.Method(ReaderPort, "ReadAsync"), census.Method(SessionFactoryPort, "OpenAsync"), census.Method(SessionPort, "ReadMembershipsAsync") })
            Dirty(census, BorrowerOffenders, () => AddCall(census.Type(HttpMinter).Module, target));
        Dirty(census, BorrowerOffenders, () => RemoveCall(census.Body(
            "LearnStack.Modules.Identity.Application.Authorization.MembershipAdministrationAuthorization", "RecheckAsync"), ReaderPort, "ReadAsync"));
        Dirty(census, BorrowerOffenders, () => RemoveCall(census.Body(
            "LearnStack.Modules.Identity.Application.Authorization.IdentityAdministrationReadAuthorization", "ReadFactsAsync"), ReaderPort, "ReadAsync"));
        foreach (var port in new[] { ReaderPort, SessionFactoryPort, SessionPort })
            Dirty(census, BorrowerOffenders, () =>
            {
                var module = census.Type(Reader).Module;
                var extra = new TypeDefinition("AuthorizationControl", "AdditionalReaderOwner", TypeAttributes.NotPublic | TypeAttributes.Sealed, module.TypeSystem.Object);
                extra.Interfaces.Add(new InterfaceImplementation(module.ImportReference(census.Type(port))));
                module.Types.Add(extra);
                return () => module.Types.Remove(extra);
            });
        foreach (var wrapped in new[] { false, true })
            Dirty(census, BorrowerOffenders, () =>
            {
                var source = census.Type(Source);
                var raw = source.Fields.Single(field => field.Name == "_source").FieldType;
                var returned = wrapped ? new GenericInstanceType(source.Module.ImportReference(typeof(Task<>))) : raw;
                if (returned is GenericInstanceType generic) generic.GenericArguments.Add(raw);
                var method = new MethodDefinition("RawSourceAlias", MethodAttributes.Public, returned);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                source.Methods.Add(method);
                return () => source.Methods.Remove(method);
            });
        Dirty(census, BorrowerOffenders, () =>
        {
            var source = census.Type(Source);
            var method = new MethodDefinition("RunCallerSql", MethodAttributes.Public, source.Module.TypeSystem.Void);
            method.Parameters.Add(new ParameterDefinition(source.Module.ImportReference(typeof(Action))));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            source.Methods.Add(method);
            return () => source.Methods.Remove(method);
        });
        Dirty(census, BorrowerOffenders, () =>
        {
            var source = census.Type(Source);
            var method = new MethodDefinition("BoxedSourceAlias", MethodAttributes.Public, source.Module.TypeSystem.Object);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, source.Fields.Single(field => field.Name == "_source")));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            source.Methods.Add(method);
            return () => source.Methods.Remove(method);
        });
    }

    [Fact]
    public void Authorization_owner_awaits_the_real_mode_statement_before_exact_candidate_announcement()
    {
        using var census = Census.Read();
        SetupOffenders(census).Should().BeEmpty("Fix: await the real READ ONLY round trip before the six exact candidate/reset GUCs, through an executing helper (ADR-0076)");
        var open = census.Body(Owner, "OpenAsync");
        var mode = open.Body.Instructions.Single(instruction => instruction.Operand as string == "SET TRANSACTION READ ONLY");
        Dirty(census, SetupOffenders, () => Replace(mode, OpCodes.Ldstr, "SET TRANSACTION READ WRITE"));
        var announcement = open.Body.Instructions.Single(instruction => instruction.Operand is string text && text.StartsWith("SELECT set_config", StringComparison.Ordinal));
        Dirty(census, SetupOffenders, () => Replace(announcement, OpCodes.Ldstr,
            (string)announcement.Operand + ", set_config('app.unreviewed', 'authority', true)"));
        var calls = open.Body.Instructions.Where(instruction => IsCall(instruction, Owner, "ExecuteSetupAsync")).ToArray();
        Dirty(census, SetupOffenders, () => RemoveInstruction(AwaitAck(open, calls[0], announcement)));
        Dirty(census, SetupOffenders, () => RemoveCall(census.Body(Owner, "ExecuteSetupAsync"), "System.Data.Common.DbCommand", "ExecuteNonQueryAsync"));
        var helper = census.Body(Owner, "ExecuteSetupAsync");
        var execution = helper.Body.Instructions.Single(instruction => IsCall(instruction, "System.Data.Common.DbCommand", "ExecuteNonQueryAsync"));
        Dirty(census, SetupOffenders, () => RemoveInstruction(AwaitAck(helper, execution,
            helper.Body.Instructions.First(instruction => instruction.Offset > execution.Offset && IsCall(instruction, Owner, "DisposeStatementAsync")))));
        var construction = helper.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference target && target.DeclaringType.FullName == "Npgsql.NpgsqlCommand");
        Dirty(census, SetupOffenders, () => Replace(construction.Previous.Previous.Previous.Previous.Previous, OpCodes.Ldstr, "SELECT 1"));
    }

    [Fact]
    public void Authorization_checkout_awaits_the_shared_role_guard_with_its_deadline_before_begin()
    {
        using var census = Census.Read();
        GuardOffenders(census).Should().BeEmpty("Fix: every confined checkout awaits the shared role/reachable-bypass guard with DeadlineToken before BEGIN (ADR-0076; Standard 05)");
        var open = census.Body(Owner, "OpenAsync");
        var guard = open.Body.Instructions.Single(instruction => IsCall(instruction, RoleGuard, "RefuseBypassRoleAsync"));
        var begin = open.Body.Instructions.Single(instruction => IsCall(instruction, "Npgsql.NpgsqlConnection", "BeginTransactionAsync"));
        Dirty(census, GuardOffenders, () => RemoveInstruction(guard));
        Dirty(census, GuardOffenders, () => RemoveInstruction(AwaitAck(open, guard, begin)));
        Dirty(census, GuardOffenders, () => RemoveInstruction(guard.Previous));
        Dirty(census, GuardOffenders, () => AddCall(census.Type(HttpMinter).Module, census.Method(RoleGuard, "RefuseBypassRoleAsync")));
    }

    [Fact]
    public void Authorization_reader_executes_only_bounded_nonlocking_identity_and_same_child_lifecycle_statements()
    {
        using var census = Census.Read();
        QueryOffenders(census).Should().BeEmpty("Fix: use only the exact bounded Identity statement and Tenancy's separate statement on the same child; never profiles, Users, locks or an ambient owner (ADR-0076)");
        var read = census.Body(Reader, "ReadMembershipsAsync");
        var sql = read.Body.Instructions.Single(instruction => instruction.Operand is string text && text.StartsWith("WITH eligible_memberships", StringComparison.Ordinal));
        foreach (var dirty in new[]
        {
            ((string)sql.Operand).Replace("LIMIT 3", "LIMIT 2", StringComparison.Ordinal),
            ((string)sql.Operand).Replace("LIMIT 9", "LIMIT 8", StringComparison.Ordinal),
            ((string)sql.Operand).Replace("member.user_id = @actor", "true", StringComparison.Ordinal),
            (string)sql.Operand + " JOIN public.users AS leaked ON true",
            (string)sql.Operand + " JOIN public.membership_profiles AS leaked ON true",
            (string)sql.Operand + " FOR UPDATE",
            (string)sql.Operand + "; SELECT public.fn_identity_admin_admission_state(@actor,@tenant)",
        }) Dirty(census, QueryOffenders, () => Replace(sql, OpCodes.Ldstr, dirty));
        var roleLimit = RoleMaterializationLimits(read).Single();
        Dirty(census, QueryOffenders, () => Replace(roleLimit.Bound, OpCodes.Ldc_I4, 9));
        Dirty(census, QueryOffenders, () => RemoveInstruction(roleLimit.Count));
        Dirty(census, QueryOffenders, () => RemoveInstruction(roleLimit.Branch));
        Dirty(census, QueryOffenders, () => RemoveInstruction(roleLimit.Throw));
        var lifecycle = census.Body(Lifecycle, "ReadAuthorizationAsync");
        var ambient = census.Type(Lifecycle).Fields.Single(field => field.FieldType.FullName == "LearnStack.SharedKernel.Persistence.IUnitOfWork");
        Dirty(census, QueryOffenders, () =>
        {
            var injected = Instruction.Create(OpCodes.Ldfld, ambient);
            lifecycle.Body.Instructions.Insert(0, injected);
            return () => lifecycle.Body.Instructions.Remove(injected);
        });
    }

    private static List<string> CandidateOffenders(Census census)
    {
        var errors = new List<string>();
        var candidate = census.Type(Candidate);
        if (!candidate.IsSealed || candidate.Methods.Where(method => method.IsConstructor).Any(method => !method.IsPrivate)
            || candidate.Methods.Count(method => method.IsConstructor) != 1
            || !candidate.Fields.Any(field => field.Name == "_source" && field.IsPrivate && field.IsInitOnly && field.FieldType.FullName == Minter)
            || !candidate.Fields.Any(field => field.Name == "_evidence" && field.IsPrivate && field.IsInitOnly))
            errors.Add("Candidate construction and provenance fields are private and closed");
        Set(errors, "candidate minters", census.Types.Where(type => census.IsDerivedFrom(type, Minter)).Select(type => type.FullName), [HttpMinter]);
        Callers(census, errors, Candidate, ".ctor", [Minter + ".ForCandidate"]);
        Callers(census, errors, Minter, "ForCandidate", [HttpMinter + ".ForValidatedScope"]);
        if (Calls(census.Body(Candidate, "get_IsActive"), Minter, "get_IsActive").Count() != 1
            || Calls(census.Body(Candidate, "get_IsActive"), Minter, "MatchesEvidence").Count() != 1
            || Calls(census.Body(Minter, "ForCandidate"), "System.ObjectDisposedException", "ThrowIf").Count() != 1
            || Calls(census.Body(Minter, "Dispose"), "System.Threading.Interlocked", "Exchange").Count() != 1)
            errors.Add("A closed or replaced source cannot remain active or mint another candidate");
        var http = census.Type(HttpMinter);
        var factory = census.Method(HttpMinter, "ForValidatedScope");
        if (!http.IsSealed || http.IsPublic || !factory.IsAssembly || factory.IsStatic
            || Calls(census.Body(HttpMinter, "ForValidatedScope"), "LearnStack.Api.Authentication.TenantSubjectAdmissionProvenance", "Matches").Count() != 1
            || EvidenceComparisons(census.Body(HttpMinter, "IsEvidenceCurrent")).Count() != 6
            || Calls(census.Body(HttpMinter, "IsEvidenceCurrent"), "LearnStack.Api.Authentication.TenantSubjectAdmissionProvenance", "Matches").Count() != 1)
            errors.Add("The HTTP source retains exact normalized binding, quota admission and current feature ownership");
        return errors;
    }

    private static List<string> BorrowerOffenders(Census census)
    {
        var errors = OwnNavigationOffenders(census);
        Callers(census, errors, Source, "OpenAsync", [Reader + ".OpenAsync"]);
        Callers(census, errors, ReaderPort, "ReadAsync", ["LearnStack.Modules.Identity.Application.Authorization.IdentityOperationAuthorizer.AuthorizeAsync",
            "LearnStack.Modules.Identity.Application.Authorization.RoleGrantAuthorization.RecheckAsync",
            "LearnStack.Modules.Identity.Application.Authorization.MembershipAdministrationAuthorization.RecheckAsync",
            "LearnStack.Modules.Identity.Application.Authorization.IdentityAdministrationReadAuthorization.ReadFactsAsync",
            "LearnStack.Api.Authentication.LiveTenantMembershipReader.CoversAsync"]);
        Callers(census, errors, LifecyclePort, "ReadAuthorizationAsync", [Orchestrator + ".ReadAsync"]);
        Callers(census, errors, SessionFactoryPort, "OpenAsync", [Orchestrator + ".ReadAsync"]);
        Callers(census, errors, SessionPort, "ReadMembershipsAsync", [Orchestrator + ".ReadAsync"]);
        Callers(census, errors, SessionPort, "get_Unit", [Orchestrator + ".ReadAsync"], distinct: true);
        Callers(census, errors, SessionPort, "get_DeadlineToken", [Orchestrator + ".ReadAsync"], distinct: true);
        Callers(census, errors, SessionPort, "CompleteAsync", [Orchestrator + ".ReadAsync"]);
        if (Calls(census.Body(Orchestrator, "ReadAsync"), "System.IAsyncDisposable", "DisposeAsync").Count() != 1)
            errors.Add("The committed-state orchestrator retains its real asynchronous session cleanup");
        Callers(census, errors, SessionPort, "MarkCleanupFailed", [Orchestrator + ".ReadAsync"]);
        Callers(census, errors, Reader, "ReadMembershipsAsync", [Session + ".ReadMembershipsAsync"]);
        Callers(census, errors, Session, ".ctor", [Reader + ".OpenAsync"]);
        Callers(census, errors, Lifecycle, "ReadAuthorizationAsync", []);
        Callers(census, errors, Owner, ".ctor", [Source + ".RetainOwner"]);
        Callers(census, errors, Owner, "OpenAsync", [Source + ".OpenAsync", Source + ".QualifyCoreAsync"]);
        Callers(census, errors, Owner, "CompleteAsync", [Source + ".QualifyCoreAsync", Session + ".CompleteAsync"]);
        Callers(census, errors, Owner, "get_Unit", [Session + ".get_Unit", Reader + ".ReadMembershipsAsync"], distinct: true);
        Set(errors, "authorization reader implementations", census.Types.Where(type => type.Interfaces.Any(item => item.InterfaceType.FullName == ReaderPort))
            .Select(type => type.FullName), [Orchestrator]);
        Set(errors, "authorization session factories", census.Types.Where(type => type.Interfaces.Any(item => item.InterfaceType.FullName == SessionFactoryPort))
            .Select(type => type.FullName), [Reader]);
        Set(errors, "authorization sessions", census.Types.Where(type => type.Interfaces.Any(item => item.InterfaceType.FullName == SessionPort))
            .Select(type => type.FullName), [Session]);
        var session = census.Type(Session);
        if (!session.IsNestedPrivate || !session.IsSealed || session.Fields.Count != 2
            || session.Fields.Any(field => !field.IsPrivate || !field.IsInitOnly || field.IsStatic)
            || !session.Fields.Select(field => field.FieldType.FullName).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { Owner, Candidate }.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            errors.Add("The private session retains only its immutable actual owner and candidate");
        if (ProviderReferencesTenancy(census.Type(Reader))
            || Il.ReferencedTypeNames(census.Type(Orchestrator)).Any(name => name.StartsWith("Npgsql.", StringComparison.Ordinal)
                || name.StartsWith("LearnStack.Infrastructure.", StringComparison.Ordinal)))
            errors.Add("Only Application coordinates the cross-module lifecycle contract; provider ownership remains in Infrastructure");
        foreach (var type in new[] { census.Type(Source), census.Type(Owner) })
        {
            if (!type.IsSealed || type.Fields.Any(field => field.FieldType.FullName == "Npgsql.NpgsqlDataSource" && (!field.IsPrivate || !field.IsInitOnly)))
                errors.Add("The authorization source and actual owner are sealed and hold only private readonly datasource fields");
            foreach (var method in type.Methods.Where(method => method.IsPublic || method.IsAssembly))
                if ((!method.IsConstructor && (ExposesRawSource(method.ReturnType) || ExposesCallback(method.ReturnType)
                        || method.Parameters.Any(parameter => ExposesRawSource(parameter.ParameterType))))
                    || method.Parameters.Any(parameter => ExposesCallback(parameter.ParameterType)))
                    errors.Add("No raw datasource alias or caller SQL/callback surface: " + method.FullName);
            if (Il.ReferencedTypeNames(type).Intersect([
                    "System.IServiceProvider", "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory", "MediatR.ISender",
                    "LearnStack.Modules.Identity.Domain.User", "LearnStack.Modules.Identity.Domain.MembershipProfile"], StringComparer.Ordinal).Any())
                errors.Add("The confined source cannot locate/dispatch business work or read global/profile projections: " + type.FullName);
        }
        foreach (var (type, readers) in new[]
        {
            (Source, new[] { census.Method(Source, "RetainOwner").FullName, census.Method(Source, "DisposeCoreAsync").FullName }),
            (Owner, new[] { census.Method(Owner, "OpenAsync").FullName }),
        })
        {
            var field = census.Type(type).Fields.Single(item => item.FieldType.FullName == "Npgsql.NpgsqlDataSource");
            if (census.Bodies.Any(item => item.Body.Body.Instructions.Any(instruction => instruction.Operand is FieldReference target
                    && target.FullName == field.FullName && instruction.OpCode != OpCodes.Stfld
                    && (instruction.OpCode != OpCodes.Ldfld || !readers.Contains(item.Owner.FullName, StringComparer.Ordinal)))))
                errors.Add("Only exact lifecycle members may read the private physical source: " + field.FullName);
        }
        return errors;
    }

    private static bool ProviderReferencesTenancy(TypeDefinition provider) => AllProviderTypes(provider)
        .SelectMany(Il.ReferencedTypeReferences).Any(reference =>
            reference.FullName.StartsWith("LearnStack.Modules.Tenancy.", StringComparison.Ordinal)
            || AssemblyOf(reference).StartsWith("LearnStack.Modules.Tenancy.", StringComparison.Ordinal));

    // Include declared private sessions and helpers as well as compiler-generated
    // state machines. The module's separate navigation registration is not a reader.
    private static IEnumerable<TypeDefinition> AllProviderTypes(TypeDefinition provider)
    {
        yield return provider;
        foreach (var nested in provider.NestedTypes.SelectMany(AllProviderTypes)) yield return nested;
    }

    private static void AssertProviderTenancyBoundaryControls(Census census)
    {
        var provider = census.Type(Reader);
        var declared = new TypeDefinition(string.Empty, "BoundaryHelper", TypeAttributes.NestedPrivate | TypeAttributes.Sealed,
            provider.Module.TypeSystem.Object);
        var nested = new TypeDefinition(string.Empty, "NestedBoundaryHelper", TypeAttributes.NestedPrivate | TypeAttributes.Sealed,
            provider.Module.TypeSystem.Object);
        var generated = new TypeDefinition(string.Empty, "<BoundaryProbe>", TypeAttributes.NestedPrivate | TypeAttributes.Sealed,
            provider.Module.TypeSystem.Object);
        provider.NestedTypes.Add(declared);
        declared.NestedTypes.Add(nested);
        provider.NestedTypes.Add(generated);
        try
        {
            ProviderReferencesTenancy(provider).Should().BeFalse("unrelated nested helpers are clean controls");
            BorrowerOffenders(census).Should().BeEmpty();
            foreach (var owner in new[] { provider, census.Type(Session), nested, generated })
            {
                foreach (var call in new[] { false, true })
                {
                    var method = new MethodDefinition("BorrowLifecycle", MethodAttributes.Private | MethodAttributes.Static,
                        owner.Module.TypeSystem.Void);
                    method.Body.Instructions.Add(call
                        ? Instruction.Create(OpCodes.Call, owner.Module.ImportReference(census.Method(LifecyclePort, "ReadAuthorizationAsync")))
                        : Instruction.Create(OpCodes.Ldtoken, owner.Module.ImportReference(census.Type(LifecyclePort))));
                    if (!call) method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    owner.Methods.Add(method);
                    try { AssertDirty(); }
                    finally { owner.Methods.Remove(method); }
                }
                var foreign = new TypeReference("BoundaryControl", "ForeignType", owner.Module,
                    census.Type(LifecyclePort).Module.Assembly.Name);
                var field = new FieldDefinition("ForeignProviderState", FieldAttributes.Private, foreign);
                owner.Fields.Add(field);
                try { AssertDirty(); }
                finally { owner.Fields.Remove(field); }
            }
        }
        finally
        {
            provider.NestedTypes.Remove(generated);
            provider.NestedTypes.Remove(declared);
        }
        ProviderReferencesTenancy(provider).Should().BeFalse();
        BorrowerOffenders(census).Should().BeEmpty("restoring the exact reader restores its lifecycle boundary");

        void AssertDirty()
        {
            ProviderReferencesTenancy(provider).Should().BeTrue("the provider guard itself must catch body, signature and nested references");
            BorrowerOffenders(census).Should().Contain(
                "Only Application coordinates the cross-module lifecycle contract; provider ownership remains in Infrastructure");
        }
    }

    // ADR-0078/0079: current-tenant navigation derives coverage through the owning
    // module's actual reader; a public DTO or callback is never supplied as admission.
    private static void AssertOwnNavigationControls(Census census)
    {
        foreach (var (port, implementation, method) in new[]
        {
            (NavigationReaderPort, NavigationReader, "ReadAsync"),
            (NavigationCoveragePort, NavigationCoverage, "ReadCurrentAsync"),
        })
        {
            Dirty(census, BorrowerOffenders, () => AddCall(census.Type(implementation).Module, census.Method(port, method)));
            Dirty(census, BorrowerOffenders, () =>
            {
                var module = census.Type(implementation).Module;
                var extra = new TypeDefinition("AuthorizationControl", "OtherNavigationOwner", TypeAttributes.NotPublic | TypeAttributes.Sealed, module.TypeSystem.Object);
                extra.Interfaces.Add(new InterfaceImplementation(module.ImportReference(census.Type(port))));
                module.Types.Add(extra);
                return () => module.Types.Remove(extra);
            });
            Dirty(census, BorrowerOffenders, () =>
            {
                var target = census.Method(port, method);
                var supplied = new ParameterDefinition("suppliedCoverage", ParameterAttributes.None,
                    target.Module.ImportReference(census.Type(NavigationAccess)));
                target.Parameters.Add(supplied);
                return () => target.Parameters.Remove(supplied);
            });
        }
        foreach (var (owner, method, target, name) in new[]
        {
            ("LearnStack.Modules.Identity.Application.Memberships.GetOwnMembershipContextsQueryHandler", "Handle", NavigationReaderPort, "ReadAsync"),
            (NavigationReader, "ReadAsync", NavigationCoveragePort, "ReadCurrentAsync"),
            (NavigationCoverage, "ReadCurrentAsync", AdministrationReadAuthorization, "Current"),
            (NavigationCoverage, "ReadCurrentAsync", AdministrationReadAuthorization, "ReadFactsAsync"),
        }) Dirty(census, BorrowerOffenders, () => RemoveCall(census.Body(owner, method), target, name));
        var reads = Calls(census.Body(NavigationReader, "ReadAsync"), NavigationCoveragePort, "ReadCurrentAsync").ToArray();
        foreach (var read in reads)
        {
            Dirty(census, BorrowerOffenders, () => RemoveInstruction(read));
            foreach (var opcode in new[] { OpCodes.Ldftn, OpCodes.Ldvirtftn })
                Dirty(census, BorrowerOffenders, () => Replace(read, opcode, read.Operand));
        }
        var construction = census.Body(NavigationCoverage, "ReadCurrentAsync").Body.Instructions.Single(instruction =>
            instruction.Operand is MethodReference target && target.DeclaringType.FullName == NavigationAccess && target.Name == ".ctor");
        Dirty(census, BorrowerOffenders, () => RemoveInstruction(construction));
        Dirty(census, BorrowerOffenders, () => AddCall(census.Type(NavigationCoverage).Module, (MethodReference)construction.Operand));
        var navigationBody = census.Body(NavigationReader, "ReadAsync");
        var guardCalls = Calls(navigationBody, NavigationReader, "Matches").ToArray();
        var queryCall = Calls(navigationBody, "LearnStack.Modules.Tenancy.Infrastructure.Persistence.TenancyAdministrationStorageBoundary", "ReadAsync").Single();
        foreach (var guard in guardCalls) Dirty(census, BorrowerOffenders, () => RemoveInstruction(guard));
        foreach (var (start, end) in new[] { (reads[0], guardCalls[0]), (queryCall, guardCalls[1]), (reads[1], guardCalls[2]) })
            Dirty(census, BorrowerOffenders, () => RemoveInstruction(AwaitAck(navigationBody, start, end)));
        var registrations = NavigationRegistrations(census).ToArray();
        registrations.Should().HaveCount(4);
        foreach (var registration in registrations)
        {
            Dirty(census, BorrowerOffenders, () => RemoveInstruction(registration.Instruction));
            Dirty(census, BorrowerOffenders, () => AddCall(registration.Owner.Module, (MethodReference)registration.Instruction.Operand));
        }
        var coverage = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Identity/LearnStack.Modules.Identity.Application/Memberships/OwnMembershipNavigationHandlers.cs"));
        var navigation = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/Persistence/OwnTenantNavigationReader.cs"));
        OwnNavigationReadSourceIsBound(navigation).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            ("var access = admission.Value;", "var access = new OwnTenantNavigationAccess(admission.Value.Invocation, true);"),
            ("if (!Matches(access))", "if (false)"),
            ("var final = await coverage.ReadCurrentAsync(cancellationToken);", "var final = admission;"),
            ("if (final.IsFailure)", "if (false)"),
            ("!Matches(final.Value)", "false"),
            ("!ReferenceEquals(final.Value.Invocation, access.Invocation)", "false"),
            ("final.Value.HasTenantMembership != access.HasTenantMembership", "false"),
        })
        {
            var dirty = navigation.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(navigation);
            OwnNavigationReadSourceIsBound(dirty).Should().BeFalse("fresh own coverage must be awaited and compared before returning derived branches");
        }
        OwnCoverageSourceIsBound(coverage).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            ("Operation: GetOwnMembershipContextsQuery request", "Operation: GetOwnCapabilitiesQuery request"),
            ("!ReferenceEquals(authorization.Current(request), invocation)", "false"),
            ("authorization.ReadFactsAsync(request, invocation, cancellationToken)", "authorization.ReadFactsAsync(request, invocations.CurrentInvocation!, cancellationToken)"),
            ("facts.Value.Memberships.Any(member => member.OrganizationId is null)", "true"),
            ("member.OrganizationId == invocation.OrganizationId", "true"),
            ("new OwnTenantNavigationAccess(invocation, tenant)", "new OwnTenantNavigationAccess(invocation, true)"),
        })
        {
            var dirty = coverage.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(coverage);
            OwnCoverageSourceIsBound(dirty).Should().BeFalse("only the exact active navigation request and actual own facts establish derived branch coverage");
        }
    }

    private static List<string> OwnNavigationOffenders(Census census)
    {
        var errors = new List<string>();
        foreach (var (port, implementation) in new[] { (NavigationReaderPort, NavigationReader),
                     (NavigationCoveragePort, NavigationCoverage), (AdministrationReadStorePort, AdministrationReadStore) })
            Set(errors, "own navigation/read implementations of " + port,
                census.Types.Where(type => type.Interfaces.Any(item => item.InterfaceType.FullName == port)).Select(type => type.FullName), [implementation]);
        Callers(census, errors, NavigationReaderPort, "ReadAsync",
            ["LearnStack.Modules.Identity.Application.Memberships.GetOwnMembershipContextsQueryHandler.Handle"]);
        Callers(census, errors, NavigationCoveragePort, "ReadCurrentAsync", [NavigationReader + ".ReadAsync", NavigationReader + ".ReadAsync"]);
        Callers(census, errors, NavigationReader, "ReadAsync", []);
        Callers(census, errors, NavigationCoverage, "ReadCurrentAsync", []);
        Callers(census, errors, AdministrationReadAuthorization, "Current",
            [AdministrationReadAuthorization + ".AdmitAsync", NavigationCoverage + ".ReadCurrentAsync"]);
        Callers(census, errors, AdministrationReadAuthorization, "ReadFactsAsync",
            [NavigationCoverage + ".ReadCurrentAsync", "LearnStack.Modules.Identity.Application.Memberships.GetOwnCapabilitiesQueryHandler.Handle",
                "LearnStack.Modules.Identity.Application.Roles.GetPermissionRegistryQueryHandler.Handle"]);
        foreach (var (port, method, parameters, result) in new[]
        {
            (NavigationReaderPort, "ReadAsync", new[] { "System.String", "System.Int32", "System.Threading.CancellationToken" },
                "System.Threading.Tasks.Task`1<LearnStack.SharedKernel.Results.Result`1<LearnStack.SharedKernel.Pagination.Page`1<LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle.OwnTenantNavigationItem>>>"),
            (NavigationCoveragePort, "ReadCurrentAsync", new[] { "System.Threading.CancellationToken" },
                "System.Threading.Tasks.Task`1<LearnStack.SharedKernel.Results.Result`1<" + NavigationAccess + ">>"),
        })
        {
            var type = census.Type(port);
            if (!type.IsPublic || !type.IsInterface || type.Methods.Count != 1
                || census.Method(port, method).ReturnType.FullName != result
                || !census.Method(port, method).Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters))
                errors.Add("Fix: navigation ports take only cursor/limit or cancellation, never caller-supplied coverage: " + port);
        }
        foreach (var (implementation, dependencies) in new[]
        {
            (NavigationCoverage, new[] { AdministrationReadAuthorization, "LearnStack.SharedKernel.Permissions.IOperationInvocationContext" }),
            (NavigationReader, new[] { "LearnStack.Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext",
                "LearnStack.SharedKernel.Persistence.IUnitOfWork", "LearnStack.SharedKernel.Tenancy.ITenantContext",
                "LearnStack.SharedKernel.Permissions.IOperationInvocationContext", NavigationCoveragePort }),
        })
        {
            var type = census.Type(implementation);
            var constructors = type.Methods.Where(method => method.IsConstructor && !method.IsStatic).ToArray();
            if (!type.IsSealed || constructors.Length != 1
                || !constructors[0].Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(dependencies))
                errors.Add("Fix: retain the exact navigation owner dependencies without a supplied fact or resolver: " + implementation);
        }
        var constructions = census.Bodies.SelectMany(item => item.Body.Body.Instructions.Where(instruction =>
                instruction.Operand is MethodReference target && target.DeclaringType.FullName == NavigationAccess && target.Name == ".ctor"
                && target.Parameters.Count == 2).Select(instruction => (item.Owner, Instruction: instruction))).ToArray();
        Set(errors, "actual navigation coverage result producers", constructions.Select(site => site.Owner.DeclaringType.FullName + "." + site.Owner.Name),
            [NavigationCoverage + ".ReadCurrentAsync"]);
        if (constructions.Any(site => site.Instruction.OpCode != OpCodes.Newobj))
            errors.Add("Navigation coverage construction cannot become an executable alias");
        Set(errors, "navigation/read scoped registrations", NavigationRegistrations(census).Select(site =>
            site.Owner.DeclaringType.FullName + "." + site.Owner.Name + "|" + ((GenericInstanceMethod)site.Instruction.Operand).Name + "|"
                + string.Join(",", ((GenericInstanceMethod)site.Instruction.Operand).GenericArguments.Select(argument => argument.FullName))),
        [
            "LearnStack.Modules.Tenancy.Infrastructure.TenantSiteRegistration.AddTenantSiteReads|TryAddScoped|" + NavigationReaderPort + "," + NavigationReader,
            "LearnStack.Modules.Identity.Infrastructure.Bindings.IdentityFoundationRegistration.AddIdentityContextualFoundation|TryAddScoped|" + NavigationCoveragePort + "," + NavigationCoverage,
            "LearnStack.Modules.Identity.Infrastructure.Bindings.IdentityFoundationRegistration.AddIdentityContextualFoundation|TryAddScoped|" + AdministrationReadStorePort + "," + AdministrationReadStore,
            "LearnStack.Modules.Identity.Infrastructure.Authorization.IdentityAuthorizationRegistration.AddIdentityOperationPolicies|TryAddScoped|" + AdministrationReadAuthorization,
        ]);
        if (NavigationRegistrations(census).Any(site => site.Instruction.OpCode != OpCodes.Call
                || site.Instruction.Operand is not GenericInstanceMethod target || target.HasThis
                || target.DeclaringType.FullName != "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions"
                || AssemblyOf(target.DeclaringType) != "Microsoft.Extensions.DependencyInjection.Abstractions"
                || target.ReturnType.FullName != "System.Void"
                || !target.Parameters.Select(parameter => parameter.ParameterType.FullName)
                    .SequenceEqual(["Microsoft.Extensions.DependencyInjection.IServiceCollection"])))
            errors.Add("Navigation registrations must remain actual scoped DI calls");
        var body = census.Body(NavigationReader, "ReadAsync");
        var coverage = Calls(body, NavigationCoveragePort, "ReadCurrentAsync").ToArray();
        var read = Calls(body, "LearnStack.Modules.Tenancy.Infrastructure.Persistence.TenancyAdministrationStorageBoundary", "ReadAsync").ToArray();
        var matches = Calls(body, NavigationReader, "Matches").ToArray();
        if (coverage.Length != 2 || read.Length != 1 || matches.Length != 3
            || !HasAwaitAck(body, coverage[0], matches[0]) || !HasAwaitAck(body, read[0], matches[1])
            || !HasAwaitAck(body, coverage[1], matches[2])
            || body.Body.Instructions.IndexOf(matches[0]) >= body.Body.Instructions.IndexOf(read[0])
            || body.Body.Instructions.IndexOf(matches[1]) >= body.Body.Instructions.IndexOf(coverage[1]))
            errors.Add("Fix: await actual coverage before reading and await fresh coverage after reading before returning navigation");
        return errors;
    }

    private static IEnumerable<(MethodDefinition Owner, Instruction Instruction)> NavigationRegistrations(Census census) =>
        census.Bodies.SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is GenericInstanceMethod target
                && target.GenericArguments.Any(argument => argument.FullName is NavigationReaderPort or NavigationCoveragePort
                    or AdministrationReadStorePort or AdministrationReadAuthorization))
            .Select(instruction => (item.Owner, instruction)));

    private static bool OwnNavigationReadSourceIsBound(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var readers = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "OwnTenantNavigationReader").ToArray();
        if (root.ContainsDiagnostics || readers.Length != 1) return false;
        var methods = readers[0].Members.OfType<MethodDeclarationSyntax>().Where(method => method.Identifier.ValueText == "ReadAsync").ToArray();
        if (methods.Length != 1 || methods[0].Body is not { } body) return false;
        var read = body.Statements.OfType<TryStatementSyntax>().ToArray();
        return read.Length == 1 && read[0].Block.Statements.Count == 9
            && body.Statements.Count >= 5
            && SyntaxFactory.AreEquivalent(body.Statements[1], SyntaxFactory.ParseStatement("var admission = await coverage.ReadCurrentAsync(cancellationToken);"))
            && SyntaxFactory.AreEquivalent(body.Statements[2], SyntaxFactory.ParseStatement("if (admission.IsFailure) return Result<Page<OwnTenantNavigationItem>>.Fail(admission.Error);"))
            && SyntaxFactory.AreEquivalent(body.Statements[3], SyntaxFactory.ParseStatement("var access = admission.Value;"))
            && SyntaxFactory.AreEquivalent(body.Statements[4], SyntaxFactory.ParseStatement("if (!Matches(access)) return Fail(\"lockey_forbidden\");"))
            && SyntaxFactory.AreEquivalent(read[0].Block.Statements[3], SyntaxFactory.ParseStatement("var final = await coverage.ReadCurrentAsync(cancellationToken);"))
            && SyntaxFactory.AreEquivalent(read[0].Block.Statements[4], SyntaxFactory.ParseStatement("if (final.IsFailure) return Result<Page<OwnTenantNavigationItem>>.Fail(final.Error);"))
            && SyntaxFactory.AreEquivalent(read[0].Block.Statements[5], SyntaxFactory.ParseStatement("""
                if (!Matches(final.Value) || !ReferenceEquals(final.Value.Invocation, access.Invocation)
                    || final.Value.HasTenantMembership != access.HasTenantMembership) return Fail("lockey_not_found");
                """));
    }

    private static bool OwnCoverageSourceIsBound(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var providers = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(type => type.Identifier.ValueText == "OwnTenantNavigationCoverage").ToArray();
        if (root.ContainsDiagnostics || providers.Length != 1) return false;
        var methods = providers[0].Members.OfType<MethodDeclarationSyntax>().ToArray();
        return methods.Length == 1 && methods[0].Identifier.ValueText == "ReadCurrentAsync"
            && SyntaxFactory.AreEquivalent(methods[0].Body, SyntaxFactory.ParseStatement("""
            {
                if (invocations.CurrentInvocation is not { Operation: GetOwnMembershipContextsQuery request } invocation
                    || !ReferenceEquals(authorization.Current(request), invocation))
                    return IdentityAdministrationReadAuthorization.Fail<OwnTenantNavigationAccess>("lockey_forbidden");
                var facts = await authorization.ReadFactsAsync(request, invocation, cancellationToken);
                if (facts.IsFailure) return Result<OwnTenantNavigationAccess>.Fail(facts.Error);
                var tenant = facts.Value.Memberships.Any(member => member.OrganizationId is null);
                if (!tenant && !facts.Value.Memberships.Any(member => member.OrganizationId == invocation.OrganizationId))
                    return IdentityAdministrationReadAuthorization.Fail<OwnTenantNavigationAccess>("lockey_not_found");
                return Result.Ok(new OwnTenantNavigationAccess(invocation, tenant));
            }
            """));
    }

    private static List<string> SetupOffenders(Census census)
    {
        var errors = new List<string>();
        var body = census.Body(Owner, "OpenAsync");
        var instructions = body.Body.Instructions;
        var setup = instructions.Where(instruction => IsCall(instruction, Owner, "ExecuteSetupAsync")).ToArray();
        var mode = instructions.Where(instruction => instruction.Operand as string == "SET TRANSACTION READ ONLY").ToArray();
        var announcement = instructions.Where(instruction => instruction.Operand is string text && text.StartsWith("SELECT set_config", StringComparison.Ordinal)).ToArray();
        if (setup.Length != 2 || mode.Length != 1 || announcement.Length != 1
            || instructions.IndexOf(mode[0]) >= instructions.IndexOf(setup[0])
            || instructions.IndexOf(setup[0]) >= instructions.IndexOf(announcement[0])
            || instructions.IndexOf(announcement[0]) >= instructions.IndexOf(setup[1])
            || !HasAwaitAck(body, setup[0], announcement[0]))
            errors.Add("The real mode round trip is awaited before announcing the candidate");
        if (announcement.Length == 1)
        {
            var settings = Regex.Matches((string)announcement[0].Operand,
                @"set_config\s*\(\s*'([^']+)'\s*,\s*([^,]+),\s*true\s*\)", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value + ":" + match.Groups[2].Value.Trim());
            Set(errors, "candidate GUCs", settings, ["app.tenant_id:@tenant", "app.organization_id:@organization", "app.scope:''",
                "app.resolving_host:''", "app.identity_authority_id:''", "app.identity_subject_key:''"]);
            var publish = instructions.Where(instruction => instruction.OpCode == OpCodes.Newobj
                && instruction.Operand is MethodReference target && target.DeclaringType.FullName == "LearnStack.SharedKernel.Persistence.TenantContextAnnouncement").ToArray();
            if (setup.Length != 2 || publish.Length != 1 || !HasAwaitAck(body, setup[1], publish[0]))
                errors.Add("Announcement evidence is published only after the actual GUC acknowledgement");
        }
        var helper = census.Body(Owner, "ExecuteSetupAsync");
        var executions = Calls(helper, "System.Data.Common.DbCommand", "ExecuteNonQueryAsync").ToArray();
        var disposals = Calls(helper, Owner, "DisposeStatementAsync").ToArray();
        var constructors = helper.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference target && target.DeclaringType.FullName == "Npgsql.NpgsqlCommand"
            && AssemblyOf(target.DeclaringType) == "Npgsql").ToArray();
        if (executions.Length != 1 || disposals.Length == 0 || !HasAwaitAck(helper, executions[0], disposals[0])
            || constructors.Length != 1 || constructors[0].Next?.OpCode != OpCodes.Stfld
            || constructors[0].Next?.Operand is not FieldReference commandField
            || commandField.FieldType.FullName != "Npgsql.NpgsqlCommand"
            || constructors[0].Previous?.Previous?.Previous?.Previous?.Previous?.Operand is not FieldReference statement
            || statement.Name != "statement" || statement.FieldType.FullName != "System.String"
            || !IsCall(executions[0].Previous, Owner, "get_DeadlineToken")
            || executions[0].Previous?.Previous?.Previous?.Operand is not FieldReference receiver
            || receiver.FullName != commandField.FullName)
            errors.Add("The setup helper executes and awaits its actual caller statement; a no-op/name-only helper is forbidden");
        return errors;
    }

    private static List<string> GuardOffenders(Census census)
    {
        var errors = new List<string>();
        Callers(census, errors, RoleGuard, "RefuseBypassRoleAsync", [Owner + ".OpenAsync",
            "LearnStack.Infrastructure.Persistence.DatabaseQualificationProof.OpenAsync",
            "LearnStack.Infrastructure.Persistence.AuthorizationEvidenceSession.OpenAsync"]);
        var body = census.Body(Owner, "OpenAsync");
        var guards = Calls(body, RoleGuard, "RefuseBypassRoleAsync").ToArray();
        var begins = Calls(body, "Npgsql.NpgsqlConnection", "BeginTransactionAsync").ToArray();
        if (guards.Length != 1 || begins.Length != 1 || !HasAwaitAck(body, guards[0], begins[0])
            || !IsCall(guards[0].Previous, Owner, "get_DeadlineToken"))
            errors.Add("Actual checkout awaits the shared guard with DeadlineToken before BEGIN");
        var forward = census.Body(RoleGuard, "RefuseBypassRoleAsync");
        var bridge = census.Method(RoleGuard, "RefuseBypassRoleAsync");
        if (!bridge.IsAssembly || !bridge.IsStatic || bridge.Parameters.Count != 2
            || bridge.Parameters[0].ParameterType.FullName != "Npgsql.NpgsqlConnection"
            || bridge.Parameters[1].ParameterType.FullName != "System.Threading.CancellationToken"
            || Calls(forward, RoleGuard, "RefuseBypassRole").Count() != 1
            || !forward.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Ldarg_1))
            errors.Add("The shared role-guard bridge forwards the actual operation token");
        var constructors = census.Type(Source).Methods.Where(method => method.IsConstructor && method.IsPublic).ToArray();
        if (constructors.Length != 1 || constructors[0].Parameters.Count != 1
            || constructors[0].Parameters[0].ParameterType.FullName != "System.String"
            || !constructors[0].Body.Instructions.Any(instruction => IsCall(instruction, Source, ".ctor")
                && instruction.Previous?.OpCode == OpCodes.Ldnull && instruction.Previous.Previous?.OpCode == OpCodes.Ldc_I4_4))
            errors.Add("Deployment callers cannot widen the four-owner source bound");
        return errors;
    }

    private static List<string> QueryOffenders(Census census)
    {
        var errors = new List<string>();
        var reader = census.Body(Reader, "ReadMembershipsAsync");
        var lifecycle = census.Body(Lifecycle, "ReadAuthorizationAsync");
        var identitySql = Sql(reader).SingleOrDefault(text => text.StartsWith("WITH eligible_memberships", StringComparison.Ordinal));
        var lifecycleSql = Sql(lifecycle).SingleOrDefault(text => text.StartsWith("SELECT t.id", StringComparison.Ordinal));
        if (identitySql is null || !identitySql.Contains("LIMIT 3", StringComparison.Ordinal) || !identitySql.Contains("LIMIT 9", StringComparison.Ordinal)
            || !identitySql.Contains("member.user_id = @actor", StringComparison.Ordinal)
            || !identitySql.Contains("member.tenant_id = @tenant", StringComparison.Ordinal)
            || !identitySql.Contains("member.organization_id = @organization", StringComparison.Ordinal)
            || !identitySql.Contains("transaction_read_only", StringComparison.Ordinal)
            || !identitySql.Contains("transaction_isolation", StringComparison.Ordinal))
            errors.Add("The Identity statement remains exact, committed-mode checked and bounded with excess detection");
        if (identitySql is not null) Set(errors, "Identity tables", Tables(identitySql),
            ["memberships", "membership_role_assignments", "roles", "role_permission_grants"]);
        if (RoleMaterializationLimits(reader).Count() != 1)
            errors.Add("The actual role-ID set must reject more than eight materialized IDs with the fixed unavailable exception");
        if (lifecycleSql is null || !lifecycleSql.Contains("transaction_read_only", StringComparison.Ordinal)
            || !lifecycleSql.Contains("transaction_isolation", StringComparison.Ordinal))
            errors.Add("Lifecycle validates the actual server transaction mode");
        if (lifecycleSql is not null) Set(errors, "Tenancy tables", Tables(lifecycleSql), ["tenants", "organizations"]);
        foreach (var method in new[] { reader, lifecycle })
        {
            if (Calls(method, "Npgsql.NpgsqlCommand", "ExecuteReaderAsync").Count() != 1
                || method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference target
                    && target.DeclaringType.FullName == "Npgsql.NpgsqlCommand" && target.Name is "ExecuteScalarAsync" or "ExecuteNonQueryAsync"))
                errors.Add("Each module runs only its one exact statement: " + method.FullName);
            foreach (var sql in Sql(method))
                if (Regex.IsMatch(sql, @"\b(?:INSERT|UPDATE|DELETE|pg_advisory\w*|pg_try_advisory\w*|fn_identity_\w*)\b|\bFOR\s+(?:SHARE|KEY|NO)\b", RegexOptions.IgnoreCase))
                    errors.Add("Authorization SQL has no writes or business row/advisory/owner-function locks");
        }
        if (lifecycle.Body.Instructions.Any(instruction => instruction.Operand is FieldReference field && field.DeclaringType.FullName == Lifecycle))
            errors.Add("The lifecycle authorization overload never reads its captured ambient business unit");
        var forbiddenTypes = new[] { "LearnStack.SharedKernel.Tenancy.ITenantContext", "Npgsql.NpgsqlDataSource",
            "LearnStack.Modules.Identity.Domain.User", "LearnStack.Modules.Identity.Domain.MembershipProfile", "System.IServiceProvider", "MediatR.ISender" };
        foreach (var component in new[] { Reader, Orchestrator })
            if (Il.ReferencedTypeNames(census.Type(component)).Intersect(forbiddenTypes, StringComparer.Ordinal).Any())
                errors.Add("The reader has no profile/global identity, ambient context, raw datasource or dispatch capability: " + component);
        foreach (var (type, expected) in new[] { (Reader, 1), (Source, 0), (Owner, 2), (Lifecycle, 2) })
            if (census.Bodies.Where(item => item.Owner.DeclaringType.FullName == type)
                .Sum(item => item.Body.Body.Instructions.Count(instruction => instruction.OpCode == OpCodes.Newobj
                    && instruction.Operand is MethodReference target && target.DeclaringType.FullName == "Npgsql.NpgsqlCommand")) != expected)
                errors.Add("No extra SQL helper is admitted on " + type);
        return errors;
    }

    private static IEnumerable<string> Sql(MethodDefinition body) => body.Body.Instructions
        .Where(instruction => instruction.OpCode == OpCodes.Ldstr).Select(instruction => (string)instruction.Operand)
        .Where(text => text.StartsWith("SELECT", StringComparison.Ordinal) || text.StartsWith("WITH", StringComparison.Ordinal));

    // All nine-role stored graphs necessarily repeat a finite template today. This
    // independently pins the actual materialization guard without claiming its >8 path
    // executes before the duplicate-template guard in those database controls.
    private static IEnumerable<RoleMaterializationLimit> RoleMaterializationLimits(MethodDefinition method)
    {
        const string RoleIds = "System.Collections.Generic.HashSet`1<System.Guid>";
        foreach (var count in Calls(method, RoleIds, "get_Count"))
        {
            if (count.Previous?.OpCode != OpCodes.Ldfld || count.Previous.Operand is not FieldReference set
                || set.DeclaringType.FullName != method.DeclaringType.FullName || set.FieldType.FullName != RoleIds
                || !set.Name.StartsWith("<roleIds>", StringComparison.Ordinal)
                || Calls(method, RoleIds, "Add").Count(add => add.Previous?.Previous?.OpCode == OpCodes.Ldfld
                    && add.Previous.Previous.Operand is FieldReference receiver && receiver.FullName == set.FullName) != 1)
                continue;
            var bound = count.Next;
            var branch = bound?.Next;
            var refused = branch?.Next;
            var terminal = refused?.Next;
            if (bound?.OpCode != OpCodes.Ldc_I4_8 || branch?.OpCode.Code is not (Code.Ble or Code.Ble_S)
                || !IsCall(refused, Reader, "Unavailable") || terminal?.OpCode != OpCodes.Throw
                || branch.Operand is not Instruction admitted || admitted != terminal.Next)
                continue;
            yield return new(count, bound, branch, terminal);
        }
    }

    private sealed record RoleMaterializationLimit(Instruction Count, Instruction Bound, Instruction Branch, Instruction Throw);

    // Release Roslyn lowers ReferenceEquals to reference-identity branches. Match each
    // actual live getter, captured witness getter and rejecting comparison, not a method name.
    private static IEnumerable<Instruction> EvidenceComparisons(MethodDefinition method)
    {
        var witness = HttpMinter + "/CredentialWitness";
        foreach (var (captured, liveType, liveGetter, feature) in new[]
        {
            ("get_Http", "Microsoft.AspNetCore.Http.IHttpContextAccessor", "get_HttpContext", ""),
            ("get_Host", "Microsoft.AspNetCore.Http.Features.IFeatureCollection", "Get", "LearnStack.Api.Tenancy.HostClassification"),
            ("get_Identity", "LearnStack.Api.Authentication.TenantCredentialFeature", "get_Identity", ""),
            ("get_Subject", "LearnStack.Api.Authentication.TenantCredentialFeature", "get_VerifiedSubject", ""),
            ("get_Decision", "Microsoft.AspNetCore.Http.Features.IFeatureCollection", "Get", "LearnStack.Api.Authentication.SubjectAdmissionDecision"),
            ("get_Provenance", "Microsoft.AspNetCore.Http.Features.IFeatureCollection", "Get", "LearnStack.Api.Authentication.TenantSubjectAdmissionProvenance"),
        })
        {
            var matches = Calls(method, witness, captured).Where(getter =>
                IsCall(getter.Previous?.Previous, liveType, liveGetter)
                && (feature.Length == 0 || getter.Previous!.Previous!.Operand is GenericInstanceMethod generic
                    && generic.GenericArguments.SingleOrDefault()?.FullName == feature))
                .Select(getter => getter.Next).Where(next => next is not null && RejectsChangedReference(next)).ToArray();
            if (matches.Length == 1) yield return matches[0]!;
        }
    }

    private static bool RejectsChangedReference(Instruction comparison)
    {
        var branch = IsCall(comparison, "System.Object", "ReferenceEquals") ? comparison.Next : comparison;
        if (branch is null || !(comparison.OpCode.Code is Code.Bne_Un or Code.Bne_Un_S
            || IsCall(comparison, "System.Object", "ReferenceEquals") && branch.OpCode.Code is Code.Brfalse or Code.Brfalse_S))
            return false;
        return branch.Operand is Instruction rejected && rejected.OpCode == OpCodes.Ldc_I4_0
            && rejected.Next?.OpCode == OpCodes.Ret;
    }
    private static IEnumerable<string> Tables(string sql) => Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+public\.([a-z_]+)", RegexOptions.IgnoreCase)
        .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal);
    private static bool ExposesRawSource(TypeReference type) => type.FullName is "Npgsql.NpgsqlDataSource" or "System.Data.Common.DbDataSource"
        or "Npgsql.NpgsqlConnection" or "System.Data.Common.DbConnection"
        || type is GenericInstanceType generic && generic.GenericArguments.Any(ExposesRawSource)
        || type is TypeSpecification specification && ExposesRawSource(specification.ElementType);
    private static bool ExposesCallback(TypeReference type) => type.FullName.StartsWith("System.Action", StringComparison.Ordinal)
        || type.FullName.StartsWith("System.Func", StringComparison.Ordinal) || type is GenericInstanceType generic && generic.GenericArguments.Any(ExposesCallback);
    private static IEnumerable<Instruction> Calls(MethodDefinition body, string type, string method) => body.Body.Instructions.Where(instruction => IsCall(instruction, type, method));
    private static bool IsCall(Instruction? instruction, string type, string method) => instruction?.OpCode.FlowControl == FlowControl.Call
        && instruction.Operand is MethodReference target && target.DeclaringType.FullName == type && target.Name == method;
    private static bool HasAwaitAck(MethodDefinition body, Instruction start, Instruction end) => body.Body.Instructions
        .Skip(body.Body.Instructions.IndexOf(start) + 1).TakeWhile(instruction => instruction != end).Any(IsAwaitAck)
        && body.Body.Instructions.IndexOf(start) < body.Body.Instructions.IndexOf(end);
    private static bool IsAwaitAck(Instruction instruction) => instruction.OpCode.FlowControl == FlowControl.Call && instruction.Operand is MethodReference target
        && target.Name == "GetResult" && target.DeclaringType.FullName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal)
        && target.DeclaringType.Name.Contains("Awaiter", StringComparison.Ordinal);
    private static Instruction AwaitAck(MethodDefinition body, Instruction start, Instruction end) => body.Body.Instructions
        .Skip(body.Body.Instructions.IndexOf(start) + 1).TakeWhile(instruction => instruction != end).First(IsAwaitAck);

    private static void Callers(Census census, List<string> errors, string type, string method, string[] expected, bool distinct = false)
    {
        var target = census.Method(type, method);
        var sites = census.Bodies.SelectMany(item => item.Body.Body.Instructions.Where(instruction => instruction.Operand is MethodReference reference
                && reference.DeclaringType.FullName == type && reference.Name == method)
            .Select(instruction => (item.Owner, Instruction: instruction, Target: (MethodReference)instruction.Operand))).ToArray();
        var callers = sites.Select(site => site.Owner.DeclaringType.FullName + "." + site.Owner.Name);
        Set(errors, "callers of " + type + "." + method, distinct ? callers.Distinct(StringComparer.Ordinal) : callers, expected);
        if (sites.Any(site => site.Target.FullName != target.FullName || AssemblyOf(site.Target.DeclaringType) != target.DeclaringType.Module.Assembly.Name.Name
            || site.Instruction.OpCode.FlowControl != FlowControl.Call)) errors.Add("Only actual calls target the declared capability: " + target.FullName);
    }

    private static void Set(List<string> errors, string description, IEnumerable<string> actual, IEnumerable<string> expected)
    {
        if (!actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))) errors.Add("Unexpected " + description);
    }
    private static string AssemblyOf(TypeReference type) => type.Scope switch
    {
        AssemblyNameReference assembly => assembly.Name,
        ModuleDefinition module => module.Assembly.Name.Name,
        _ => type.Scope.Name,
    };
    private static void Dirty(Census census, Func<Census, List<string>> detector, Func<Action> mutate)
    {
        var restore = mutate();
        try { detector(census).Should().NotBeEmpty("the same compiled detector must reject the planted violation"); }
        finally { restore(); }
        detector(census).Should().BeEmpty("the same detector accepts the restored compiled production control");
    }
    private static Action RemoveCall(MethodDefinition body, string type, string method) => RemoveInstruction(Calls(body, type, method).First());
    private static Action RemoveInstruction(Instruction instruction) => Replace(instruction, OpCodes.Nop, null);
    private static Action Replace(Instruction instruction, OpCode opcode, object? operand)
    {
        var originalCode = instruction.OpCode;
        var originalOperand = instruction.Operand;
        instruction.OpCode = opcode;
        instruction.Operand = operand;
        return () => { instruction.OpCode = originalCode; instruction.Operand = originalOperand; };
    }
    private static Action AddCall(ModuleDefinition module, MethodReference target)
    {
        var type = new TypeDefinition("AuthorizationControl", "BorrowedCapability", TypeAttributes.NotPublic, module.TypeSystem.Object);
        var method = new MethodDefinition("BorrowAsync", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, module.ImportReference(target)));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        module.Types.Add(type);
        return () => module.Types.Remove(type);
    }

    private sealed class Census : IDisposable
    {
        private Census(ModuleDefinition[] modules) => Modules = modules;
        private ModuleDefinition[] Modules { get; }
        public IEnumerable<TypeDefinition> Types => Modules.SelectMany(module => module.GetTypes());
        public IEnumerable<(MethodDefinition Owner, MethodDefinition Body)> Bodies => Types.Where(type => !type.Name.Contains('<', StringComparison.Ordinal))
            .SelectMany(type => Il.Methods(type).Where(item => item.Definition.HasBody).Select(item =>
                (Origin(type, item.Definition, item.DeclaredAs), item.Definition)));
        public TypeDefinition Type(string name) => Types.Single(type => type.FullName == name);
        public MethodDefinition Method(string type, string name) => Type(type).Methods.Single(method => method.Name == name);
        public MethodDefinition Body(string type, string name) => Il.Methods(Type(type)).Where(item => item.DeclaredAs == name && item.Definition.HasBody)
            .Select(item => item.Definition).OrderByDescending(method => method.Name == "MoveNext").First();
        public bool IsDerivedFrom(TypeDefinition type, string expected)
        {
            for (var parent = type.BaseType; parent is not null;)
            {
                if (parent.FullName == expected) return true;
                parent = Types.SingleOrDefault(item => item.FullName == parent.FullName)?.BaseType;
            }
            return false;
        }
        public static Census Read() => new([.. ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location))]);
        public void Dispose() { foreach (var module in Modules) module.Dispose(); }

        private static MethodDefinition Origin(TypeDefinition type, MethodDefinition body, string declaredAs)
        {
            if (body.DeclaringType == type) return body;
            var actual = type.Methods.Where(method => method.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                && attribute.ConstructorArguments.SingleOrDefault().Value is TypeReference state
                && state.FullName == body.DeclaringType.FullName)).ToArray();
            if (actual.Length == 1) return actual[0];
            var named = type.Methods.Where(method => method.Name == declaredAs).ToArray();
            return named.Length == 1 ? named[0] : body;
        }
    }
}
