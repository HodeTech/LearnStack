using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Census = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

/// <summary>Structural census only; actual interruption and competing-commit qualification belongs to the database proofs.</summary>
public sealed class SeedContinuationConventionTests
{
    private const string Custom = "LearnStack.Modules.Customization.Application.Seeding.SeedCustomizationWriteGuard";
    private const string Education = "LearnStack.Modules.Education.Application.Seeding.SeedEducationWriteGuard";
    private const string CustomPort = "LearnStack.Modules.Customization.Application.Seeding.ISeedCustomizationWriteGuard";
    private const string EducationPort = "LearnStack.Modules.Education.Application.Seeding.ISeedEducationWriteGuard";
    private const string CustomSource = "LearnStack.Modules.Customization.Application.Contracts.Seeding.ISeedCustomizationContinuationSource";
    private const string EducationSource = "LearnStack.Modules.Education.Application.Contracts.Seeding.ISeedEducationContinuationSource";
    private const string FixedSource = "LearnStack.Tools.Seeder.FixedSeedContentContinuationSource";
    private const string Stage = "LearnStack.Modules.Identity.Infrastructure.Persistence.SeedIdentityStageReader";
    private const string StagePort = "LearnStack.Modules.Identity.Application.Contracts.Seeding.ISeedIdentityStageReader";
    private const string CustomState = "LearnStack.Modules.Customization.Application.Abstractions.ISeedCustomizationState";
    private const string EducationState = "LearnStack.Modules.Education.Application.Abstractions.ISeedEducationState";
    private const string CustomGuardPath = "Modules/Customization/LearnStack.Modules.Customization.Application/Seeding/SeedCustomizationWriteGuard.cs";
    private const string EducationGuardPath = "Modules/Education/LearnStack.Modules.Education.Application/Seeding/SeedEducationWriteGuard.cs";
    private const string StagePath = "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/Persistence/SeedIdentityStageReader.cs";
    private const string CustomStatePath = "Modules/Customization/LearnStack.Modules.Customization.Infrastructure/Persistence/SeedCustomizationState.cs";
    private const string EducationStatePath = "Modules/Education/LearnStack.Modules.Education.Infrastructure/Persistence/SeedEducationState.cs";
    private const string FixedSourcePath = "LearnStack.Tools.Seeder/FixedSeedContentContinuationSource.cs";
    private const string ApiPath = "LearnStack.Api/Composition/PersistenceCompositionExtensions.cs";
    private const string SeedCompositionPath = "LearnStack.Tools.Seeder/SeedComposition.cs";
    private const string SeedSourcePath = "LearnStack.Tools.Seeder/SeedOperationSource.cs";
    private static readonly Writer[] Writers =
    [
        new("Customization", "Customization", "InitializeTenantContentTypeDefault", "TenantContentType.Create", "customization.default.initialize_content_type"),
        new("Customization", "Customization", "InitializeTenantLevelTaxonomyDefault", "TenantLevelTaxonomy.Create", "customization.default.initialize_taxonomy"),
        new("Customization", "Customization", "RegisterTenantContentType", "TenantContentType.Create", "customization.seed.register_content_type"),
        new("Customization", "Customization", "PublishTenantContentType", "successor.Publish", "customization.seed.publish_content_type"),
        new("Customization", "Customization", "RegisterTenantLevelTaxonomy", "TenantLevelTaxonomy.Create", "customization.seed.register_taxonomy"),
        new("Customization", "Customization", "PublishTenantLevelTaxonomy", "successor.Publish", "customization.seed.publish_taxonomy"),
        new("Education", "Courses", "CreateCourse", "Course.Create", "education.seed.create_course"),
        new("Education", "Courses", "AddCourseTranslation", "root.AddTranslation", "education.seed.add_course_translation"),
        new("Education", "Courses", "PublishCourse", "root.Publish", "education.seed.publish_course"),
        new("Education", "Lessons", "CreateLesson", "Lesson.Create", "education.seed.create_lesson"),
        new("Education", "Lessons", "AddLessonTranslation", "root.AddTranslation", "education.seed.add_lesson_translation"),
        new("Education", "Lessons", "PublishLesson", "root.Publish", "education.seed.publish_lesson"),
    ];
    private static readonly string[] OtherWriters = ["ProvisionTenantCommand", "CreateOrganizationCommand", "MapHostToTenantCommand",
        "AddTenantLocaleCommand", "SetTenantBrandingCommand", "InitializeSeedRoleCommand", "CreateFirstSeedAdminMembershipCommand",
        "CreateSeedMembershipCommand", "SetSeedMembershipProfileCommand"];
    private static readonly string[] RunnerPaths = ["LearnStack.Tools.Seeder/SeedRunner.cs", "LearnStack.Tools.Seeder/SeedIdentityFoundationRunner.cs",
        "LearnStack.Tools.Seeder/SeedAdministrationOriginRunner.cs"];
    private static readonly string[] RunnerTypes = ["LearnStack.Tools.Seeder.SeedRunner", "LearnStack.Tools.Seeder.SeedIdentityFoundationRunner",
        "LearnStack.Tools.Seeder.SeedAdministrationOriginRunner"];
    private static readonly string[] SourcePaths = [CustomGuardPath, EducationGuardPath, StagePath, CustomStatePath, EducationStatePath,
        FixedSourcePath, ApiPath, SeedCompositionPath, SeedSourcePath,
        "Modules/Customization/LearnStack.Modules.Customization.Application.Contracts/Seeding/SeedCustomizationDeclaration.cs",
        "Modules/Education/LearnStack.Modules.Education.Application.Contracts/Seeding/SeedEducationDeclaration.cs"];
    private static readonly string[] CustomAwaitOrder = ["locks.AcquireAsync", "state.PrepareAsync", "tenancy.AllowsContentContinuationAsync",
        "identity.HasNoRoleHistoryAsync", "state.ReadContentTypesAsync", "state.ReadTaxonomiesAsync"];
    private static readonly string[] EducationAwaitOrder = ["locks.AcquireAsync", "state.PrepareAsync", "tenancy.AllowsContentContinuationAsync",
        "identity.HasNoRoleHistoryAsync", "customization.IsCompleteAsync", "state.ReadCoursesAsync", "state.ReadLessonsAsync"];
    private static readonly string[] CustomRootSql = ["SELECT id FROM public.tenant_level_taxonomies WHERE tenant_id = @tenant AND id = @id FOR UPDATE",
        "SELECT id FROM public.tenant_content_types WHERE tenant_id = @tenant AND id = @id FOR UPDATE"];
    private static readonly string[] EducationRootSql = ["SELECT id FROM public.courses WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization AND id = @id FOR UPDATE",
        "SELECT id FROM public.lessons WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization AND id = @id FOR UPDATE"];

    [Fact]
    public void Tenant_Bound_Seed_Writes_Recheck_Under_Ordered_Fences()
    {
        using var census = Census.Read();
        var paths = SourcePaths.Concat(RunnerPaths).Concat(Writers.Select(writer => writer.Path)).Distinct(StringComparer.Ordinal);
        var source = paths.ToDictionary(path => path, path => File.ReadAllText(Path.Combine(SourceScan.SourceRoot, path)), StringComparer.Ordinal);
        AssertClean(census, source);
        Dirty(census, source, () => RemoveCall(census.Body(Writers[0].Handler, "Handle"), CustomPort, "PrepareAsync"));
        Dirty(census, source, () => RemoveCall(census.Body(Custom, "PrepareAsync"), StagePort, "HasNoRoleHistoryAsync"));
        Dirty(census, source, () =>
        {
            var owner = census.Type(FixedSource);
            var extra = new TypeDefinition("SeedContinuationControl", "UnownedSource", TypeAttributes.NotPublic, owner.Module.TypeSystem.Object);
            extra.Interfaces.Add(new InterfaceImplementation(owner.Module.ImportReference(census.Type(EducationSource))));
            owner.Module.Types.Add(extra);
            return () => owner.Module.Types.Remove(extra);
        });
        Dirty(census, source, () =>
        {
            var owner = census.Type(FixedSource);
            var extra = new MethodDefinition("UnownedPreparation", MethodAttributes.Private | MethodAttributes.Static, owner.Module.TypeSystem.Void);
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, owner.Module.ImportReference(census.Method(EducationPort, "PrepareAsync"))));
            extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            owner.Methods.Add(extra);
            return () => owner.Methods.Remove(extra);
        });
        DirtySource(census, source, EducationGuardPath, text => text.Replace("IdentityTenantLockMode.Exclusive", "IdentityTenantLockMode.Shared", StringComparison.Ordinal));
        DirtySource(census, source, CustomGuardPath, text => text.Replace(
            "_event ? IdentityTenantLockMode.Shared : IdentityTenantLockMode.Exclusive",
            "_event ? IdentityTenantLockMode.Exclusive : IdentityTenantLockMode.Shared", StringComparison.Ordinal));
        DirtySource(census, source, EducationGuardPath, text => text.Replace("create.SlugKey == course.SlugKey", "true", StringComparison.Ordinal));
        DirtySource(census, source, StagePath, text => text.Replace("db.Roles.AsNoTracking().AnyAsync(cancellationToken)",
            "db.Roles.AsNoTracking().AnyAsync(role => role.DeletedAt == null, cancellationToken)", StringComparison.Ordinal));
        DirtySource(census, source, ApiPath, text => text.Replace("TryAddSingleton<ISeedEducationContinuationSource, DenyAllSeedEducationContinuationSource>",
            "TryAddSingleton<ISeedEducationContinuationSource, FixedSeedContentContinuationSource>", StringComparison.Ordinal));
        DirtySource(census, source, Writers[6].Path, text => text.Replace("seedGuard.IsCurrent(request)", "true", StringComparison.Ordinal));
        DirtySource(census, source, FixedSourcePath, text => text.Replace("Map(content.ContentType.DisplayName)", "content.ContentType.DisplayName", StringComparison.Ordinal));
    }

    private static List<string> Offenders(Census census, Dictionary<string, string> source)
    {
        var errors = new List<string>();
        var methods = census.Methods;
        var calls = methods.SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (Method: method, Target: (MethodReference)instruction.Operand, Instruction: instruction))).ToArray();
        foreach (var (port, module) in new[] { (CustomPort, "Customization"), (EducationPort, "Education") })
        {
            Exact(port, "PrepareAsync", Writers.Where(writer => writer.Module == module).Select(writer => writer.Handler + "::Handle"));
            Exact(port, "IsCurrent", Writers.Where(writer => writer.Module == module).SelectMany(writer =>
                Enumerable.Repeat(writer.Handler + "::Handle", writer.Name == "RegisterTenantContentType" ? 2 : 1)));
        }
        Exact(StagePort, "HasNoRoleHistoryAsync", [Custom + "::PrepareAsync", Education + "::PrepareAsync"]);
        Exact(CustomState, "PrepareAsync", [Custom + "::PrepareAsync"]);
        Exact(EducationState, "PrepareAsync", [Education + "::PrepareAsync"]);
        Exact("LearnStack.Modules.Customization.Application.Contracts.Seeding.ISeedCustomizationPrerequisiteReader", "IsCompleteAsync", [Education + "::PrepareAsync"]);
        Implementations(CustomPort, [Custom]);
        Implementations(EducationPort, [Education]);
        Implementations(StagePort, [Stage]);
        Implementations(CustomState, ["LearnStack.Modules.Customization.Infrastructure.Persistence.SeedCustomizationState"]);
        Implementations(EducationState, ["LearnStack.Modules.Education.Infrastructure.Persistence.SeedEducationState"]);
        Implementations("LearnStack.Modules.Customization.Application.Contracts.Seeding.ISeedCustomizationPrerequisiteReader", [Custom]);
        Implementations(CustomSource, [FixedSource, "LearnStack.Modules.Customization.Application.Contracts.Seeding.DenyAllSeedCustomizationContinuationSource"]);
        Implementations(EducationSource, [FixedSource, "LearnStack.Modules.Education.Application.Contracts.Seeding.DenyAllSeedEducationContinuationSource"]);
        foreach (var writer in Writers)
        {
            Require(writer.Handler, "Handle", writer.Module == "Customization" ? CustomPort : EducationPort, "PrepareAsync", 1);
            CheckHandler(writer, Parse(source[writer.Path]), errors);
        }
        foreach (var (type, path, order) in new[] { (Custom, CustomGuardPath, CustomAwaitOrder), (Education, EducationGuardPath, EducationAwaitOrder) })
        {
            Require(type, "PrepareAsync", "LearnStack.SharedKernel.Identity.IIdentityMutationLocks", "AcquireAsync", 1);
            Require(type, "IsCurrent", "LearnStack.SharedKernel.Identity.IIdentityMutationLocks", "RequireCurrent", 1);
            CheckGuard(Parse(source[path]), order, type == Custom, errors);
        }
        Require(Stage, "HasNoRoleHistoryAsync", "LearnStack.SharedKernel.Identity.IIdentityMutationLocks", "RequireCurrent", 2);
        var participating = new[] { Custom, Education, Stage, "LearnStack.Modules.Customization.Infrastructure.Persistence.SeedCustomizationState",
            "LearnStack.Modules.Education.Infrastructure.Persistence.SeedEducationState" };
        if (calls.Any(call => participating.Any(type => call.Method.DeclaringType.FullName == type
                || call.Method.DeclaringType.FullName.StartsWith(type + "/", StringComparison.Ordinal))
            && (call.Target.Name is "BeginTransactionAsync" or "BeginGlobalIdentityTransactionAsync" or "CreateConnection"
                or "OpenConnectionAsync" or "CreateAsyncScope" or "CreateScope" or "Send" or "SendAsync"
                or "SetTenantContextAsync" or "SetProvisioningTenantContextAsync" or "VerifySeedPrerequisitesAsync" or "IgnoreQueryFilters")))
            errors.Add("Content prerequisites use the existing ordinary owner and owning contracts, without dispatch, filter override or hatch");
        var fixedSource = census.Type(FixedSource);
        if (fixedSource.Fields.Count != 2 || fixedSource.Fields.Any(field => !field.IsPrivate || !field.IsInitOnly
                || field.FieldType.GetElementType().FullName != "System.Collections.Immutable.ImmutableDictionary`2"))
            errors.Add("The source captures two independently immutable declaration dictionaries");
        var completePopulation = Writers.Select(writer => writer.Name + "Command").Concat(OtherWriters)
            .Append("AdoptSeedAdministrationOriginCommand").ToArray();
        var compiledPopulation = calls.Where(call => call.Instruction.OpCode == OpCodes.Newobj && call.Target.Name == ".ctor"
                && call.Target.DeclaringType.Name.EndsWith("Command", StringComparison.Ordinal)
                && RunnerTypes.Any(type => call.Method.DeclaringType.FullName == type
                    || call.Method.DeclaringType.FullName.StartsWith(type + "/", StringComparison.Ordinal)))
            .Select(call => call.Target.DeclaringType.Name).Distinct(StringComparer.Ordinal);
        if (!Same(compiledPopulation, completePopulation)) errors.Add("The actual compiled runner constructors retain the complete 21-plus-origin writer population");
        var compiledSource = census.Type("LearnStack.Tools.Seeder.SeedOperationSource").Methods.Single(method => method.IsConstructor && method.IsStatic)
            .Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldtoken && instruction.Operand is TypeReference type
                && type.Name.EndsWith("Command", StringComparison.Ordinal)).Select(instruction => ((TypeReference)instruction.Operand).Name);
        if (!Same(compiledSource, completePopulation.Append("SetDefaultTenantLocaleCommand")))
            errors.Add("The actual finite source has no missing or extra tenant-bound writer capability");
        CheckPopulation(source, errors);
        CheckSourceAndComposition(source, errors);
        CheckStage(Parse(source[StagePath]), errors);
        CheckStates(source, errors);
        return errors;

        void Exact(string type, string name, IEnumerable<string> expected)
        {
            var actual = calls.Where(call => call.Target.DeclaringType.FullName == type && call.Target.Name == name).ToArray();
            if (actual.Any(call => call.Instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj))
                || !Same(actual.Select(call => census.Owner(call.Method)), expected)) errors.Add("Exact content caller/alias census changed: " + type + "::" + name);
        }
        void Require(string type, string method, string target, string name, int count)
        {
            if (census.Body(type, method).Body.Instructions.Count(instruction => instruction.Operand is MethodReference call
                    && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && call.DeclaringType.FullName == target && call.Name == name) != count)
                errors.Add("Missing executing content guard: " + type + "::" + method + " -> " + name);
        }
        void Implementations(string contract, string[] expected)
        {
            if (!Same(census.Types.Where(type => type.Interfaces.Any(item => item.InterfaceType.FullName == contract)).Select(type => type.FullName), expected))
                errors.Add("The production source/reader implementation census changed: " + contract);
        }
    }

    private static void CheckHandler(Writer writer, SyntaxNode root, List<string> errors)
    {
        var method = Method(root, "Handle");
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        var prepare = calls.Where(call => SameExpression(call, "seedGuard.PrepareAsync(request, cancellationToken)")).ToArray();
        var current = calls.Where(call => SameExpression(call, "seedGuard.IsCurrent(request)")).ToArray();
        var mutation = calls.Where(call => call.Expression.ToString() == writer.Mutation).ToArray();
        if (prepare.Length != 1 || current.Length == 0 || mutation.Length != 1
            || prepare[0].SpanStart > mutation[0].SpanStart || !current.Any(call => call.SpanStart > prepare[0].SpanStart && call.SpanStart < mutation[0].SpanStart)
            || prepare[0].Ancestors().OfType<IfStatementSyntax>().FirstOrDefault()?.Statement is not ReturnStatementSyntax)
            errors.Add("The fixed writer prepares and revalidates before its aggregate act: " + writer.Name);
        if (writer.Name is "PublishTenantContentType" or "PublishTenantLevelTaxonomy"
            && !Has(method, "request.RequireNoIncumbent && incumbent is not null"))
            errors.Add("The fixed publication cannot enter its ordinary incumbent-retirement branch: " + writer.Name);
    }

    private static void CheckGuard(SyntaxNode root, string[] order, bool customization, List<string> errors)
    {
        var prepare = Method(root, "PrepareAsync");
        var awaits = prepare.DescendantNodes().OfType<AwaitExpressionSyntax>().ToArray();
        if (!awaits.Select(expression => ((InvocationExpressionSyntax)expression.Expression).Expression.ToString()).SequenceEqual(order))
            errors.Add("The complete Tenant fence precedes module locks and fresh owning-contract prerequisites");
        foreach (var awaited in awaits)
        {
            var statement = awaited.Ancestors().OfType<StatementSyntax>().First(item => item.Parent == prepare.Body);
            var index = prepare.Body!.Statements.IndexOf(statement);
            if (index + 1 >= prepare.Body.Statements.Count || !SyntaxFactory.AreEquivalent(prepare.Body.Statements[index + 1],
                    SyntaxFactory.ParseStatement("if (!IsCurrent(request)) return false;")))
                errors.Add("Every awaited fence/read rechecks actual invocation, declaration, transaction and generation");
        }
        var plan = customization
            ? "new IdentityMutationLockPlan([], context.TenantId, _event ? IdentityTenantLockMode.Shared : IdentityTenantLockMode.Exclusive)"
            : "new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Exclusive)";
        if (!Has(prepare, plan)) errors.Add("Seed uses one exclusive Tenant plan; only distinct built-in event initialization uses shared Tenant");
        foreach (var expression in new[] { "ReferenceEquals(_request, request)", "ReferenceEquals(_invocation, execution.CurrentInvocation)",
                     "ReferenceEquals(_transaction, unit.Transaction)", "unit.GetTenantContextAnnouncement(_transaction)?.Generation != _generation",
                     "ReferenceEquals(source.ForTenant(context.TenantId), _declaration)", "invocation.Policy.RequestType == request.GetType()",
                     "ReferenceEquals(invocation.Operation, request)", "ReferenceEquals(invocation.Context, context)",
                     "unit is not IOperationReadScopeView { IsTenantWideRead: true }" })
            if (!Has(root, expression)) errors.Add("Missing exact ordinary fixed-operation binding: " + expression);
        var policies = Method(root, "Policy").DescendantNodes().OfType<LiteralExpressionSyntax>().Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression)).Select(literal => literal.Token.ValueText);
        if (!Same(policies, Writers.Where(writer => writer.Module == (customization ? "Customization" : "Education")).Select(writer => writer.Policy)))
            errors.Add("The content guard owns exactly its six canonical operation policies");
        var required = customization
            ? new[] { "value.ContentTypeId == declaration.ContentType.Id", "value.Key == declaration.ContentType.Key", "Map(value.DisplayName, declaration.ContentType.DisplayName)",
                "value.JsonSchema == declaration.ContentType.JsonSchema", "value.RendererKey == declaration.ContentType.RendererKey", "value.RequireNoIncumbent",
                "value.TaxonomyId == declaration.Taxonomy.Id", "item.Metadata == declaration.Taxonomy.Items[index].Metadata", "row.SchemaRevision == expected.Items.Length" }
            : ["create.CourseId == course.Id", "create.SlugKey == course.SlugKey", "create.ContentAccess == course.ContentAccess",
                "create.LevelTaxonomyKey == course.LevelTaxonomyKey", "add.ExpectedVersion == index", "add.Title == translation.Title", "add.Summary == translation.Summary",
                "createLesson.CourseId == course.Id", "createLesson.ContentTypeSchemaVersion == lesson.ContentTypeSchemaVersion",
                "addLesson.Body == translation.Body", "publishLesson.ExpectedVersion == lesson.Translations.Length", "row.OrganizationId == expected.OrganizationId"];
        foreach (var expression in required)
            if (!Has(root, expression)) errors.Add("The fixed request/root/predecessor retains its independent pin: " + expression);
        if (customization)
        {
            if (!Has(prepare, "_event ? null : source.ForTenant(context.TenantId)")
                || !prepare.Body!.Statements.OfType<IfStatementSyntax>().Any(statement => SyntaxFactory.AreEquivalent(statement, SyntaxFactory.ParseStatement("if (_event) return true;"))))
                errors.Add("Built-in event initialization has a distinct source and no seed-history or hatch inheritance");
        }
    }

    private static void CheckPopulation(Dictionary<string, string> source, List<string> errors)
    {
        var expected = Writers.Select(writer => writer.Name + "Command").Concat(OtherWriters).ToArray();
        var constructed = RunnerPaths.SelectMany(path => Parse(source[path]).DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            .Select(creation => creation.Type.ToString()).Where(type => type.EndsWith("Command", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal);
        if (expected.Length != 21 || !Same(constructed, expected.Append("AdoptSeedAdministrationOriginCommand")))
            errors.Add("The independent actual runner census is 21 v1 writer types plus separate origin adoption");
        var requestSet = Parse(source[SeedSourcePath]).DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == "Requests").Initializer!.Value;
        var admitted = requestSet.DescendantNodesAndSelf().OfType<TypeOfExpressionSyntax>().Select(expression => expression.Type.ToString())
            .Where(type => type.EndsWith("Command", StringComparison.Ordinal));
        if (!Same(admitted, expected.Append("AdoptSeedAdministrationOriginCommand").Append("SetDefaultTenantLocaleCommand")))
            errors.Add("No missing/extra source writer hides behind the separate refused default-locale operation");
    }

    private static void CheckSourceAndComposition(Dictionary<string, string> source, List<string> errors)
    {
        var fixedRoot = Parse(source[FixedSourcePath]);
        foreach (var expression in new[] { "Map(content.ContentType.DisplayName)", "Map(content.Taxonomy.DisplayName)",
                     "Map(plain.DisplayName)", "Map(card.DisplayName)", "Map(band.DisplayName)" })
            if (!Has(fixedRoot, expression)) errors.Add("Fixed declaration maps are copied independently: " + expression);
        if (!Has(fixedRoot, "value.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)")
            || fixedRoot.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(call => Name(call) == "ToImmutableDictionary") != 3)
            errors.Add("The fixed content source captures immutable Tenant dictionaries and nested maps");
        foreach (var module in new[] { "Customization", "Education" })
        {
            var api = Parse(source[ApiPath]);
            var registration = $"services.TryAddSingleton<ISeed{module}ContinuationSource, DenyAllSeed{module}ContinuationSource>()";
            if (!Has(api, registration)) errors.Add("The API retains a denying fixed seed source: " + module);
            var declaration = Parse(source[SourcePaths.Single(path => path.EndsWith("Seed" + module + "Declaration.cs", StringComparison.Ordinal))]);
            var deny = declaration.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(type => type.Identifier.ValueText == "DenyAllSeed" + module + "ContinuationSource");
            if (deny.Members.OfType<MethodDeclarationSyntax>().Single().ExpressionBody?.Expression is not LiteralExpressionSyntax literal
                || !literal.IsKind(SyntaxKind.NullLiteralExpression)) errors.Add("The default source cannot manufacture an admission declaration");
        }
        var composition = Parse(source[SeedCompositionPath]);
        if (!Has(composition, "services.AddSingleton<FixedSeedContentContinuationSource>()")
            || composition.DescendantNodes().OfType<InvocationExpressionSyntax>().Count(call =>
                call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "GetRequiredService" } name }
                && name.TypeArgumentList.Arguments.Single().ToString() == "FixedSeedContentContinuationSource") != 2)
            errors.Add("Only the Seeder composes the same immutable declaration source behind both owning ports");
    }

    private static void CheckStage(SyntaxNode root, List<string> errors)
    {
        var read = Method(root, "HasNoRoleHistoryAsync");
        if (!Has(read, "db.Roles.AsNoTracking().AnyAsync(cancellationToken)") || read.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => Name(call) is "Where" or "IgnoreQueryFilters")
            || !Has(read, "plan.TenantMode != IdentityTenantLockMode.Exclusive") || !Has(read, "!plan.UserIds.IsEmpty")
            || !Has(read, "ReferenceEquals(unit.Transaction, transaction)") || !Has(read, "unit.GetTenantContextAnnouncement(transaction) == announcement"))
            errors.Add("The same-owner stage check includes every retained Role, without active/fixed-ID filtering or new User locks");
        var policies = Method(root, "Allowed").DescendantNodes().OfType<LiteralExpressionSyntax>().Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(literal => literal.Token.ValueText);
        if (!Same(policies, Writers.Select(writer => writer.Policy))) errors.Add("Only the twelve seed content policies consume the Role-history stage proof");
    }

    private static void CheckStates(Dictionary<string, string> source, List<string> errors)
    {
        var customization = Parse(source[CustomStatePath]);
        var education = Parse(source[EducationStatePath]);
        if (!Method(customization, "PrepareAsync").DescendantNodes().OfType<AwaitExpressionSyntax>().Select(expression =>
                ((InvocationExpressionSyntax)expression.Expression).Expression.ToString()).SequenceEqual(
                ["CustomizationConceptHistory.LockRegistrationsAsync", "CustomizationConceptHistory.LockAsync", "command.ExecuteScalarAsync"]))
            errors.Add("Customization takes quota before concept before target, beneath its already held Tenant fence");
        var cSql = customization.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
            && literal.Token.ValueText.StartsWith("SELECT ", StringComparison.Ordinal)).Select(literal => literal.Token.ValueText);
        var eSql = education.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
            && literal.Token.ValueText.StartsWith("SELECT ", StringComparison.Ordinal)).Select(literal => literal.Token.ValueText);
        if (!Same(cSql, CustomRootSql) || !eSql.SequenceEqual(EducationRootSql)) errors.Add("Root preparation uses only the module's fixed scoped parent/target locks, without SQL joins");
        foreach (var root in new[] { customization, education })
        {
            if (!Has(root, "ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)")
                || !Has(root, "ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), unit.Transaction)")
                || !Has(root, "unit.Mode != TransactionMode.ReadWrite") || !Has(root, "db.CurrentTenantWideRead"))
                errors.Add("Module preparation stays on its exact announced ordinary physical owner");
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(method => method.Identifier.ValueText.StartsWith("Read", StringComparison.Ordinal)))
                if (!method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call => Name(call) == "AsNoTracking"))
                    errors.Add("Post-wait content evidence must materialize fresh instances: " + method.Identifier.ValueText);
        }
    }

    private static SyntaxNode Parse(string text) => CSharpSyntaxTree.ParseText(text).GetRoot();
    private static MethodDeclarationSyntax Method(SyntaxNode root, string name) => root.DescendantNodes().OfType<MethodDeclarationSyntax>()
        .Single(method => method.Identifier.ValueText == name && method.Body is not null || method.Identifier.ValueText == name && method.ExpressionBody is not null);
    private static bool Has(SyntaxNode root, string expression) => root.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(node => SameExpression(node, expression));
    private static bool SameExpression(SyntaxNode node, string expression) => SyntaxFactory.AreEquivalent(node, SyntaxFactory.ParseExpression(expression));
    private static string? Name(InvocationExpressionSyntax call) => call.Expression switch
    { IdentifierNameSyntax name => name.Identifier.ValueText, MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText, _ => null };
    private static bool Same(IEnumerable<string> actual, IEnumerable<string> expected) => actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    private static void AssertClean(Census census, Dictionary<string, string> source) => Offenders(census, source).Should().BeEmpty(
        "Fix: preserve the independent writer/source census and ordered same-owner fixed continuation checks");
    private static void Dirty(Census census, Dictionary<string, string> source, Func<Action> mutation)
    {
        var undo = mutation();
        try { Offenders(census, source).Should().NotBeEmpty("the actual compiled missing/extra guard or source must fail"); }
        finally { undo(); }
        AssertClean(census, source);
    }
    private static void DirtySource(Census census, Dictionary<string, string> source, string path, Func<string, string> mutation)
    {
        var clean = source[path];
        source[path] = mutation(clean);
        source[path].Should().NotBe(clean);
        try { Offenders(census, source).Should().NotBeEmpty("the same parsed source rule rejects the planted scope/pin/history violation"); }
        finally { source[path] = clean; }
        AssertClean(census, source);
    }
    private static Action RemoveCall(MethodDefinition method, string type, string name)
    {
        var instruction = method.Body.Instructions.Single(instruction => instruction.Operand is MethodReference call
            && call.DeclaringType.FullName == type && call.Name == name);
        var opcode = instruction.OpCode;
        var operand = instruction.Operand;
        instruction.OpCode = OpCodes.Nop;
        instruction.Operand = null;
        return () => { instruction.OpCode = opcode; instruction.Operand = operand; };
    }
    private sealed record Writer(string Module, string Folder, string Name, string Mutation, string Policy)
    {
        internal string Handler => $"LearnStack.Modules.{Module}.Application.{Folder}.{Name}CommandHandler";
        internal string Path => $"Modules/{Module}/LearnStack.Modules.{Module}.Application/{Folder}/{Name}CommandHandler.cs";
    }
}
