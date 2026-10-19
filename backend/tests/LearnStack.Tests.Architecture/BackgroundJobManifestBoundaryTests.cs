using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private static readonly (string Controller, string[] Actions)[] JobManifestControllerActions =
    [
        ("LearnStack.Api.PublicReads.PublicSiteController", ["Get", "Head"]),
        ("LearnStack.Api.PublicReads.PublicEducationController", ["Catalog", "HeadCatalog", "Course", "HeadCourse", "Lesson", "HeadLesson"]),
        ("LearnStack.Api.Account.AccountController", ["GetSite"]),
    ];

    /// <summary>ADR-0047: disk-discovered modules cannot hide declarations outside the live handler scan.</summary>
    [Fact]
    public void BackgroundJob_Definitions_Stay_In_Their_Owning_Module()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, JobManifestOffenders).Should().BeEmpty();
        CheckJobFrameworkMetadata();

        var scaffold = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Modules.Media.Application");
        AssertMutation(census, "an omitted scaffold module", () =>
        {
            var index = census.Modules.IndexOf(scaffold);
            census.Modules.RemoveAt(index);
            return () => census.Modules.Insert(index, scaffold);
        }, JobManifestOffenders);

        var types = census.Types;
        foreach (var registration in new[] { typeof(BackgroundJobDefinitionRegistration), typeof(BackgroundJobRecurrenceRegistration) })
        {
            var constructor = types[(KernelAssembly, registration.FullName!)].Methods.Single(method => method.IsConstructor);
            foreach (var opcode in new[] { OpCodes.Newobj, OpCodes.Ldftn })
                AssertMutation(census, "a hidden scaffold declaration via " + opcode.Name, () =>
                    AddCallProbe(scaffold, "HiddenJobDeclaration", constructor, opcode), JobManifestOffenders);
            AssertMutation(census, "an asynchronous scaffold declaration", () =>
                AddAsyncCallProbe(scaffold, "HiddenAsyncJobDeclaration", constructor), JobManifestOffenders);
        }

        var definition = types[(InfrastructureAssembly, typeof(BackgroundJobDefinitionRegistry).FullName!)];
        var create = definition.Methods.Single(method => method.Name == "Create");
        AssertMutation(census, "a foreign registry construction delegate", () =>
            AddCallProbe(scaffold, "HiddenRegistryFactory", create, OpCodes.Ldftn), JobManifestOffenders);
        var actual = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Single(instruction => instruction.Operand is MethodReference target && SameType(target.DeclaringType, definition)
                && target.Name == "Create");
        AssertMutation(census, "a missing actual empty-definition factory", () =>
            ReplaceInstruction(actual, OpCodes.Nop, null!), JobManifestOffenders);

        // The real Hangfire capability, not a route-name substring or invented test method.
        var dashboard = typeof(Hangfire.DashboardOptions).Assembly.GetType("Hangfire.DashboardOptions", throwOnError: true)!
            .GetConstructor(Type.EmptyTypes)!;
        AssertMutation(census, "dashboard middleware capability in an otherwise unregistered module", () =>
            AddCallProbe(scaffold, "HiddenDashboard", scaffold.ImportReference(dashboard), OpCodes.Newobj), JobManifestOffenders);
        // Pinned corresponding source exposes this optional-options overload in
        // Hangfire.AspNetCore, deliberately absent from the production package set.
        // Plant its real signature as IL, without adding a dashboard dependency.
        var dashboardType = new TypeReference("Hangfire", "HangfireEndpointRouteBuilderExtensions", scaffold,
            new AssemblyNameReference("Hangfire.AspNetCore", new Version(1, 8, 25, 0)));
        var dashboardMap = new MethodReference("MapHangfireDashboard",
            scaffold.ImportReference(typeof(Microsoft.AspNetCore.Builder.IEndpointConventionBuilder)), dashboardType);
        foreach (var parameter in new[] { typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder), typeof(Hangfire.DashboardOptions), typeof(Hangfire.JobStorage) })
            dashboardMap.Parameters.Add(new ParameterDefinition(scaffold.ImportReference(parameter)));
        AssertMutation(census, "a dashboard mapping with vendor-default options", () =>
            AddCallProbe(scaffold, "HiddenDashboardMapping", dashboardMap, OpCodes.Call), JobManifestOffenders);
        var mapPost = typeof(Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions).GetMethod("MapPost",
            [typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder), typeof(string), typeof(Delegate)])!;
        AssertMutation(census, "a conditional or unregistered-module route outside the served Development path", () =>
            AddAsyncCallProbe(scaffold, "HiddenProductionRoute", scaffold.ImportReference(mapPost)), JobManifestOffenders);
        AssertMutation(census, "an undiscovered controller in a scaffold module", () =>
        {
            var controller = AddProbe(scaffold, "UnexpectedRecoveryController");
            return () => scaffold.Types.Remove(controller);
        }, JobManifestOffenders);
        var middleware = typeof(Microsoft.AspNetCore.Builder.UseMiddlewareExtensions).GetMethods()
            .Single(method => method.Name == "UseMiddleware" && method.IsGenericMethodDefinition);
        var useDashboard = new GenericInstanceMethod(scaffold.ImportReference(middleware));
        useDashboard.GenericArguments.Add(new TypeReference("Hangfire.Dashboard", "AspNetCoreDashboardMiddleware", scaffold,
            dashboardType.Scope));
        AssertMutation(census, "dashboard activation hidden in a generic middleware argument", () =>
            AddCallProbe(scaffold, "HiddenGenericDashboard", useDashboard, OpCodes.Call), JobManifestOffenders);
        foreach (var siteController in new[] { types[("LearnStack.Api", "LearnStack.Api.PublicReads.PublicSiteController")],
                     types[("LearnStack.Api", "LearnStack.Api.Account.AccountController")],
                     types[("LearnStack.Api", "LearnStack.Api.Common.ApiControllerBase")] })
            AssertMutation(census, "a new declared or inherited public action on an approved controller", () =>
        {
            var action = new MethodDefinition("UnexpectedRecovery", MethodAttributes.Public, siteController.Module.TypeSystem.Void);
            action.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            siteController.Methods.Add(action);
            return () => siteController.Methods.Remove(action);
        }, JobManifestOffenders);
    }

    private static SortedSet<string> JobManifestOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        CheckSet(offenders, "complete job manifest project population",
            census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        var names = census.Modules.Select(module => module.Assembly.Name.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var module in Modules.Names)
            foreach (var leg in new[] { "Domain", "Application.Contracts", "Application", "Infrastructure" })
                if (!names.Contains("LearnStack.Modules." + module + "." + leg))
                    offenders.Add("Missing job manifest module leg: " + module + "." + leg);
        var types = census.Types;
        foreach (var subject in new[]
                 {
                     typeof(BackgroundJobDefinitionRegistry), typeof(BackgroundJobRecurrenceRegistry),
                     typeof(BackgroundJobContractsRegistration), typeof(BackgroundJobRuntimeRegistration),
                     typeof(BackgroundJobDefinitionRegistration), typeof(BackgroundJobRecurrenceRegistration),
                     typeof(BackgroundJobRuntime),
                 })
            if (!types.ContainsKey((subject.Assembly.GetName().Name!, subject.FullName!)))
                offenders.Add("Missing live job manifest subject: " + subject.FullName);
        if (offenders.Count != 0) return offenders;

        var bodies = OwnedBodies(census).ToArray();
        var sites = bodies.SelectMany(item => item.Body.Body.Instructions
            .Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => (item.Owner, Instruction: instruction, Target: (MethodReference)instruction.Operand))).ToArray();

        foreach (var registration in new[] { typeof(BackgroundJobDefinitionRegistration), typeof(BackgroundJobRecurrenceRegistration) })
        {
            var target = types[(KernelAssembly, registration.FullName!)];
            foreach (var site in sites.Where(site => SameType(site.Target.DeclaringType, target) && site.Target.Name == ".ctor"))
                offenders.Add("Production business/recurrence declaration: " + MethodId(site.Owner));
        }

        var definitions = types[(InfrastructureAssembly, typeof(BackgroundJobDefinitionRegistry).FullName!)];
        var recurrences = types[(InfrastructureAssembly, typeof(BackgroundJobRecurrenceRegistry).FullName!)];
        var contracts = types[(InfrastructureAssembly, typeof(BackgroundJobContractsRegistration).FullName!)];
        var runtime = types[(InfrastructureAssembly, typeof(BackgroundJobRuntimeRegistration).FullName!)];
        var control = types[(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.BackgroundJobControl")];
        Factory(definitions, ".ctor", [Method(definitions, "Create")], OpCodes.Newobj);
        Factory(definitions, "Create", [Method(definitions, "CreateEmpty")], OpCodes.Call);
        Factory(definitions, "CreateEmpty", [Lambda(contracts, "<AddLearnStackBackgroundJobContracts>b__0_0"),
            Method(recurrences, "CreateEmpty")], OpCodes.Call);
        Factory(recurrences, ".ctor", [Method(recurrences, "Create")], OpCodes.Newobj);
        Factory(recurrences, "Create", [Method(recurrences, "CreateEmpty")], OpCodes.Call);
        Factory(recurrences, "CreateEmpty", [Lambda(runtime, "<AddLearnStackBackgroundJobRuntime>b__0_0"),
            Method(control, ".ctor")], OpCodes.Call);

        foreach (var registry in new[] { definitions, recurrences })
            if (!registry.IsSealed || registry.Fields.Any(field => !field.IsStatic && !field.IsInitOnly))
                offenders.Add("Job manifest registries must remain sealed with frozen instance state: " + registry.FullName);
        foreach (var site in sites.Where(site => DashboardMethodCapability(site.Target)))
            offenders.Add("Production Hangfire dashboard capability: " + MethodId(site.Owner));
        foreach (var type in types.Values)
            if (DashboardCapability(type.BaseType) || type.Interfaces.Any(item => DashboardCapability(item.InterfaceType))
                || type.Fields.Any(field => DashboardCapability(field.FieldType)))
                offenders.Add("Production Hangfire dashboard holder: " + TypeId(type));
        CheckSet(offenders, "complete production controller population", types.Values.Where(IsController).Select(TypeId),
            JobManifestControllerActions.Select(controller => "LearnStack.Api:" + controller.Controller));
        CheckSet(offenders, "exact production controller actions", types.Values.Where(IsController)
                .SelectMany(ControllerActions).Select(MethodId),
            JobManifestControllerActions.SelectMany(controller => controller.Actions.Select(name =>
                Method(types[("LearnStack.Api", controller.Controller)], name))));
        foreach (var contract in new[] { typeof(IBackgroundJobDefinitionCatalog), typeof(IBackgroundJobRequestCatalog) })
        {
            var declaration = types[(KernelAssembly, contract.FullName!)];
            CheckSet(offenders, "closed job catalogue implementations " + contract.Name,
                types.Values.Where(type => !type.IsInterface && Derives(type, declaration, types)).Select(TypeId), [TypeId(definitions)]);
        }

        // Scan every production body, not just the Development branch executed
        // by the host proof. Exactly these four endpoint registration calls exist.
        var api = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
        var main = Method(types[("LearnStack.Api", "Program")], "<Main>$");
        var openApi = Method(types[("LearnStack.Api", "LearnStack.Api.Versioning.ApiVersioningExtensions")], "MapLearnStackOpenApi");
        var endpointBuilder = typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder);
        var approvedRoutes = new[]
        {
            (main, typeof(Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions).GetMethod("MapGet", [endpointBuilder, typeof(string), typeof(Delegate)])!),
            (main, typeof(Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions).GetMethod("MapControllers", [endpointBuilder])!),
            (openApi, typeof(Microsoft.AspNetCore.Builder.OpenApiEndpointRouteBuilderExtensions).GetMethod("MapOpenApi", [endpointBuilder, typeof(string)])!),
            (openApi, typeof(Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions).GetMethod("MapScalarApiReference",
                [endpointBuilder, typeof(string), typeof(Action<Scalar.AspNetCore.ScalarOptions>)])!),
        };
        CheckSet(offenders, "exact endpoint registration capabilities", sites.Where(site => EndpointCapability(site.Target))
                .Select(site => MethodId(site.Owner) + "|" + MethodId(site.Target) + "|" + site.Instruction.OpCode.Name),
            approvedRoutes.Select(route => route.Item1 + "|" + MethodId(api.ImportReference(route.Item2)) + "|call"));
        return offenders;

        IEnumerable<MethodDefinition> ControllerActions(TypeDefinition controller)
        {
            var current = controller;
            var visited = new HashSet<(string Assembly, string Type)>();
            while (visited.Add(Key(current)))
            {
                foreach (var method in current.Methods.Where(method => method.IsPublic && !method.IsStatic && !method.IsConstructor
                             && !method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Microsoft.AspNetCore.Mvc.NonActionAttribute")))
                    yield return method;
                if (current.BaseType is not { } parent || !types.TryGetValue(Key(parent), out var known)) yield break;
                current = known;
            }
        }

        bool IsController(TypeDefinition type)
        {
            if (!(type.IsPublic || type.IsNestedPublic) || type.IsAbstract
                || type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Microsoft.AspNetCore.Mvc.NonControllerAttribute")) return false;
            if (type.Name.EndsWith("Controller", StringComparison.Ordinal)
                || type.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "Microsoft.AspNetCore.Mvc.ControllerAttribute")) return true;
            var parent = type.BaseType;
            var visited = new HashSet<(string Assembly, string Type)>();
            while (parent is not null && visited.Add(Key(parent)))
            {
                if (parent.FullName is "Microsoft.AspNetCore.Mvc.ControllerBase" or "Microsoft.AspNetCore.Mvc.Controller") return true;
                parent = types.TryGetValue(Key(parent), out var known) ? known.BaseType : null;
            }
            return false;
        }

        string Method(TypeDefinition type, string name)
        {
            var methods = type.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length == 1) return MethodId(methods[0]);
            offenders.Add("Missing/ambiguous manifest factory: " + type.FullName + "." + name);
            return "missing manifest factory";
        }
        string Lambda(TypeDefinition registration, string name)
        {
            var generated = registration.NestedTypes.Where(type => type.Name == "<>c").ToArray();
            if (generated.Length == 1) return Method(generated[0], name);
            offenders.Add("Missing exact manifest registration factory: " + registration.FullName);
            return "missing registration factory";
        }
        void Factory(TypeDefinition target, string name, string[] expected, OpCode opcode)
        {
            var actualSites = sites.Where(site => SameType(site.Target.DeclaringType, target) && site.Target.Name == name).ToArray();
            CheckSet(offenders, "exact manifest factory " + target.FullName + "." + name,
                actualSites.Select(site => MethodId(site.Owner)), expected);
            if (actualSites.Any(site => site.Instruction.OpCode != opcode))
                offenders.Add("Manifest factory cannot escape through an indirect/delegate path: " + target.FullName + "." + name);
        }
    }

    private static bool DashboardCapability(TypeReference? type) => type is not null
        && (type.FullName.StartsWith("Hangfire.Dashboard", StringComparison.Ordinal)
            || type.FullName is "Hangfire.DashboardOptions" or "Hangfire.DashboardMiddleware"
            || type.FullName.Contains("HangfireApplicationBuilderExtensions", StringComparison.Ordinal)
            || type.FullName == "Hangfire.HangfireEndpointRouteBuilderExtensions"
            || type is GenericInstanceType generic && generic.GenericArguments.Any(DashboardCapability)
            || type is TypeSpecification specification && DashboardCapability(specification.ElementType));

    private static bool DashboardMethodCapability(MethodReference method) => DashboardCapability(method.DeclaringType)
        || DashboardCapability(method.ReturnType) || method.Parameters.Any(parameter => DashboardCapability(parameter.ParameterType))
        || method is GenericInstanceMethod generic && generic.GenericArguments.Any(DashboardCapability);

    private static bool EndpointCapability(MethodReference method) => method.Name.StartsWith("Map", StringComparison.Ordinal)
        && (method.DeclaringType.Namespace.StartsWith("Microsoft.AspNetCore.Builder", StringComparison.Ordinal)
            || method.DeclaringType.Namespace == "Scalar.AspNetCore"
            || DashboardCapability(method.DeclaringType));
}
