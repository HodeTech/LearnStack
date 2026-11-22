using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0106 § 9: the retained own data-request scope is the consent subject's model applied to data requests. These
/// rules pin its closed owners: the one definer caller, the one HTTP source that observes the scope and mints the
/// witness, the one resolver that builds its context, the one physical owner, the witness's private construction and
/// binding callers, the closed set of request types each policy kind admits, and the one source registration.
/// </summary>
public sealed class OwnDataRequestScopeConventionTests
{
    private const string Witness = "LearnStack.SharedKernel.Identity.OwnDataRequestScopeWitness";
    private const string Unit = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
    private const string Lookup = "LearnStack.Modules.Identity.Infrastructure.Persistence.OwnDataRequestScopeLookup";
    private const string Source = "LearnStack.Api.Authentication.OwnDataRequestSubjectSource";

    [Fact]
    public void Own_data_request_witness_owner_and_lookup_have_closed_shapes()
    {
        var types = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).ToArray();
        var policies = new IdentityOperationPolicySource().Policies;
        policies.Where(policy => policy.Kind == OperationPolicyKind.OwnDataRequestEntry).Select(policy => policy.RequestType)
            .Should().BeEquivalentTo([typeof(ListRetainedDataRequestScopesQuery), typeof(ListRetainedDataRequestsQuery),
                typeof(CreateRetainedDataExportCommand), typeof(CancelRetainedDataRequestCommand), typeof(MintRetainedDataExportTicketCommand),
                typeof(ReadRetainedDataExportChunkCommand), typeof(ReadRetainedDataRequestReceiptQuery)],
                "the retained entry reaches exactly its six operations and the receipt read of its two keyed ones");
        policies.Where(policy => policy.Kind == OperationPolicyKind.OwnDataRequest).Select(policy => policy.RequestType)
            .Should().BeEquivalentTo([typeof(CreateOwnDataExportCommand), typeof(ListOwnDataRequestsQuery), typeof(GetOwnDataRequestQuery),
                typeof(CancelOwnDataRequestCommand), typeof(RevokeOwnDataExportCommand), typeof(MintOwnDataExportTicketCommand),
                typeof(ReadOwnDataExportChunkCommand), typeof(ReadOwnDataRequestReceiptQuery)], "the live kind is unchanged and never a retained type");
        policies.Where(policy => policy.Kind is OperationPolicyKind.OwnDataRequest or OperationPolicyKind.OwnDataRequestEntry)
            .Should().OnlyContain(policy => policy.Alternatives.IsEmpty && policy.RequiredPermission == null,
                "neither kind carries an assignable key (ADR-0106 § 4)");
        types.Where(type => !type.IsAbstract && typeof(OwnDataRequestScopeWitness.MintingSource).IsAssignableFrom(type))
            .Should().Equal(typeof(OwnDataRequestSubjectSource));
        types.Where(type => !type.IsAbstract && typeof(IOwnDataRequestScopeSource).IsAssignableFrom(type))
            .Should().Equal(typeof(OwnDataRequestSubjectSource));
        types.Where(type => !type.IsAbstract && typeof(IOwnDataRequestUnitOfWork).IsAssignableFrom(type))
            .Should().Equal(typeof(NpgsqlUnitOfWork));
        types.Where(type => !type.IsAbstract && typeof(IOwnDataRequestScopeLookup).IsAssignableFrom(type))
            .Select(type => type.FullName).Should().Equal(Lookup);
        typeof(OwnDataRequestScopeWitness).IsSealed.Should().BeTrue();
        typeof(OwnDataRequestScopeWitness).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().ContainSingle().Which.IsPrivate.Should().BeTrue();
        typeof(OwnDataRequestScopeWitness).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
        typeof(OwnDataRequestScopeWitness).GetProperty(nameof(OwnDataRequestScopeWitness.SubjectKey))!
            .GetCustomAttributes().Select(attribute => attribute.GetType().Name).Should().Contain("PiiSensitiveAttribute");
        Enum.GetNames<OwnDataRequestPurpose>().Should().Equal("List", "Create", "Read", "Cancel", "Ticket", "Chunk");
        Enum.IsDefined(TenantContextOrigin.HostAndOwnDataRequestScope).Should().BeTrue();
        new[] { OperationPolicyKind.OwnDataRequest, OperationPolicyKind.OwnDataRequestEntry }
            .Select(kind => (int)kind).Should().Equal(10, 11);
    }

    [Fact]
    public void Own_data_request_witness_callers_are_closed_and_detect_an_extra_minter_or_binding_caller()
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try
        {
            string[] Callers(string targetType, string member) => modules.SelectMany(module => module.Types)
                .SelectMany(Flatten).Where(type => !type.Name.Contains('<', StringComparison.Ordinal)
                    && !type.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute"))
                .SelectMany(type => Il.Methods(type).Where(method => method.Definition.HasBody)
                    .SelectMany(method => method.Definition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                        && target.DeclaringType.FullName == targetType && target.Name == member)
                        .Select(_ => type.FullName + "." + method.DeclaredAs))).Distinct().ToArray();
            Callers(Witness + "/MintingSource", "Capture").Should().Equal(Source + ".ForInvocation");
            Callers(Source, "SelectMutationReceipt").Should().Equal("LearnStack.Api.Idempotency.ProtectedReplayHttpAdmission.AuthorizeAsync");
            Callers(Source, "PrepareAsync").Should().Equal("LearnStack.Api.Tenancy.TenantResolverMiddleware.InvokeAsync");
            Callers(Source, "DownloadCredential").Should().Equal("LearnStack.Api.Authentication.HttpDataExportSessionBinding.CaptureRetained");
            Callers(Witness, ".ctor").Should().Equal(Witness + "/MintingSource.Capture");
            Callers(Witness, "TryBindTo").Should().Equal(Unit + ".SetTenantContextCoreAsync");
            Callers(Witness, "IsBoundTo").Should().BeEquivalentTo(Unit + ".SetTenantContextCoreAsync",
                Unit + ".ValidateOperationReadScope", Unit + ".IsOwnDataRequestOwner");
            Callers("LearnStack.SharedKernel.Persistence.IOwnDataRequestUnitOfWork", "BeginOwnDataRequestTransactionAsync")
                .Should().Equal("LearnStack.Application.Persistence.TransactionExecutionCoordinator.ExecuteCoreAsync");
            Callers("LearnStack.SharedKernel.Persistence.IOwnDataRequestUnitOfWork", "IsOwnDataRequestOwner")
                .Should().BeEquivalentTo("LearnStack.Application.Persistence.TransactionExecutionCoordinator.ExecuteFrameAsync",
                    "LearnStack.Modules.Identity.Application.DataRequests.RetainedDataRequestAdmission.For");
            Callers("LearnStack.SharedKernel.Tenancy.TenantContextFactory", "CreateForOwnDataRequestScope")
                .Should().Equal(["LearnStack.Api.Tenancy.TenantResolverMiddleware.InvokeAsync"],
                    "only the native retained-entry resolver builds this context");
            Callers("LearnStack.Modules.Identity.Application.Contracts.DataRequests.IOwnDataRequestScopeLookup", "LookupAsync")
                .Should().Equal([Source + ".PrepareAsync"], "only the native retained-entry source observes the retained groups");

            var module = modules.Single(item => item.Assembly.Name.Name == "LearnStack.SharedKernel");
            var witnessType = module.Types.SelectMany(Flatten).Single(type => type.FullName == Witness);
            var minter = witnessType.NestedTypes.Single(type => type.Name == "MintingSource");
            TypeDefinition[] Minters() => modules.SelectMany(item => item.Types).SelectMany(Flatten)
                .Where(type => !type.IsAbstract && type.BaseType?.FullName == minter.FullName).ToArray();
            Minters().Select(type => type.FullName).Should().Equal(Source);
            var rogueMinter = new TypeDefinition("ReviewProbe", "DataRequestMinter", Mono.Cecil.TypeAttributes.Class, minter);
            module.Types.Add(rogueMinter);
            Minters().Select(type => type.FullName).Should().Contain("ReviewProbe.DataRequestMinter").And.HaveCount(2);
            module.Types.Remove(rogueMinter);
            Minters().Select(type => type.FullName).Should().Equal(Source);
            var target = witnessType.Methods.Single(method => method.Name == "TryBindTo");
            var probe = new TypeDefinition("ReviewProbe", "DataRequestBinding", Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
            var method = new MethodDefinition("Bind", Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            probe.Methods.Add(method); module.Types.Add(probe);
            Callers(Witness, "TryBindTo").Should().Contain("ReviewProbe.DataRequestBinding.Bind");
            module.Types.Remove(probe);
            Callers(Witness, "TryBindTo").Should().Equal(Unit + ".SetTenantContextCoreAsync");
        }
        finally { foreach (var module in modules) module.Dispose(); }
    }

    /// <summary>The definer has one caller outside its migration: the confined lookup. Its literal is the census.</summary>
    [Fact]
    public void Only_the_confined_lookup_calls_the_retained_scope_definer()
    {
        const string Function = "fn_identity_own_data_request_scopes";
        SourceScan.FilesContaining(SourceScan.SourceRoot, Function, except: null).Order(StringComparer.Ordinal)
            .Should().Equal("Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/OwnDataRequestScopeDdl.cs",
                "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/OwnDataRequestScopeLookup.cs");
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/OwnDataRequestScopeLookup.cs"));
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var statements = root.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.Token.ValueText.Contains(Function, StringComparison.Ordinal))
            .Select(literal => string.Join(" ", literal.Token.ValueText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .ToArray();
        statements.Should().Equal(new[] { "SELECT tenant_id, organization_id FROM public.fn_identity_own_data_request_scopes("
            + "@actor,@tenant,@host_organization,@mode,@organization,@after,@limit)" },
            "the one statement reads the function's two result columns and nothing else");
    }

    /// <summary>
    /// The source's request feature is the very request it captured: a replaced feature, a different captured request or a
    /// receipt selection outside a keyed purpose must not retain authority (the consent subject's rule).
    /// </summary>
    [Fact]
    public void Retained_entry_request_feature_is_bound_to_the_captured_request()
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Api/Authentication/OwnDataRequestSubjectSource.cs"));
        RequestFeatureIsBound(source).Should().BeTrue();
        foreach (var (before, after) in new[]
        {
            ("ReferenceEquals(http.Features.Get<OwnDataRequestEntryRequest>(), _requestFeature)", "true"),
            ("ReferenceEquals(_requestFeature.Request, _request)", "true"),
            ("_native.DataRequestOperation == _purpose", "true"),
            ("_purpose?.Purpose is not (OwnDataRequestPurpose.Create or OwnDataRequestPurpose.Cancel)", "false"),
            ("_request is not (CreateRetainedDataExportCommand or CancelRetainedDataRequestCommand)", "false"),
            ("_requestFeature = new OwnDataRequestEntryRequest(receipt);", "_requestFeature = null;"),
            ("http.Features.Set(_requestFeature);\n        return true;", "GC.KeepAlive(http);\n        return true;"),
        })
        {
            var dirty = source.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(source, before);
            RequestFeatureIsBound(dirty).Should().BeFalse("a replacement feature or different captured request must not retain authority");
        }
    }

    private static bool RequestFeatureIsBound(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var owner = root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(type => type.Identifier.ValueText == "OwnDataRequestSubjectSource");
        var current = owner?.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == "IsCurrent");
        var select = owner?.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == "SelectMutationReceipt");
        var prepare = owner?.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(method => method.Identifier.ValueText == "PrepareAsync");
        if (current is null || select is null || prepare is null) return false;
        static string Tokens(Microsoft.CodeAnalysis.SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));
        var live = Tokens(current); var replay = Tokens(select); var initial = Tokens(prepare);
        return live.Contains("ReferenceEquals(http.Features.Get<OwnDataRequestEntryRequest>(),_requestFeature)", StringComparison.Ordinal)
            && live.Contains("ReferenceEquals(_requestFeature.Request,_request)", StringComparison.Ordinal)
            && live.Contains("_native.DataRequestOperation==_purpose", StringComparison.Ordinal)
            && replay.Contains("!IsCurrent(http)||_purpose?.Purposeisnot(OwnDataRequestPurpose.CreateorOwnDataRequestPurpose.Cancel)", StringComparison.Ordinal)
            && replay.Contains("_requestisnot(CreateRetainedDataExportCommandorCancelRetainedDataRequestCommand)", StringComparison.Ordinal)
            && replay.Contains("_requestFeature=newOwnDataRequestEntryRequest(receipt);http.Features.Set(_requestFeature);returntrue;", StringComparison.Ordinal)
            && initial.Contains("_requestFeature=newOwnDataRequestEntryRequest(input.Request);http.Features.Set(_requestFeature);http.Features.Set(this);", StringComparison.Ordinal);
    }

    [Fact]
    public void Retained_scope_lookup_is_scoped_in_the_api_composition_and_nowhere_else()
    {
        static string[] Registrations(string source) => CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } ? generic : null)
            .Where(generic => generic is not null && generic.Identifier.ValueText.Contains("Add", StringComparison.Ordinal)
                && generic.TypeArgumentList.Arguments.Any(argument => argument.ToString().EndsWith("IOwnDataRequestScopeLookup", StringComparison.Ordinal)
                    || argument.ToString().EndsWith("IOwnDataRequestScopeSource", StringComparison.Ordinal)))
            .Select(generic => generic!.Identifier.ValueText + "<" + string.Join(",", generic.TypeArgumentList.Arguments
                .Select(argument => argument.ToString().Split('.')[^1])) + ">")
            .ToArray();
        var api = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Api/Composition/PersistenceCompositionExtensions.cs"));
        Registrations(api).Should().Equal("TryAddScoped<IOwnDataRequestScopeLookup,OwnDataRequestScopeLookup>");
        var dirty = api + "\nclass Rogue { void Add(dynamic services) { services.AddSingleton<IOwnDataRequestScopeSource, RogueSource>(); } }";
        Registrations(dirty).Should().HaveCount(2, "an extra witness source must remain visible to the registration census");
        var seeder = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Tools.Seeder/SeedComposition.cs"));
        Registrations(seeder).Should().BeEmpty("the seeder dispatches no data request");
        var live = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Api/Authentication/LiveAuthorizationRegistration.cs"));
        Registrations(live).Should().Equal(["TryAddScoped<IOwnDataRequestScopeSource>"],
            "the one witness source is the scoped HTTP source, aliased once");
        // The registration census over every production file: exactly one IOwnDataRequestScopeSource registration.
        var everywhere = Directory.EnumerateFiles(SourceScan.SourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal))
            .SelectMany(path => Registrations(File.ReadAllText(path))).Where(item => item.Contains("IOwnDataRequestScopeSource", StringComparison.Ordinal))
            .ToArray();
        everywhere.Should().Equal(["TryAddScoped<IOwnDataRequestScopeSource>"]);
        Registrations(live + "\nclass Rogue { void Add(dynamic services) { services.AddScoped<IOwnDataRequestScopeSource, RogueSource>(); } }")
            .Should().HaveCount(2, "a second witness source must remain visible to the registration census");
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
}
