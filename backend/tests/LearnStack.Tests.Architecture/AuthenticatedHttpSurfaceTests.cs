using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0064's complete HTTP ceiling while application authorization passes through.</summary>
public sealed partial class AuthenticatedHttpSurfaceTests
{
    private const string PublicSite = "LearnStack.Api.PublicReads.PublicSiteController";
    private const string PublicEducation = "LearnStack.Api.PublicReads.PublicEducationController";
    private const string Account = "LearnStack.Api.Account.AccountController";
    private const string Versioning = "LearnStack.Api.Versioning.ApiVersioningExtensions";
    private const string Route = "Microsoft.AspNetCore.Mvc.RouteAttribute";

    private sealed record Endpoint(string Controller, string Action, string Verb, string Resource,
        string? Suffix, string Operation, Type Request);

    private static readonly Endpoint[] Endpoints =
    [
        new(PublicSite, "Get", "GET", "public/site", null, "GetPublicSite", typeof(GetPublicSiteQuery)),
        new(PublicSite, "Head", "HEAD", "public/site", null, "HeadPublicSite", typeof(GetPublicSiteQuery)),
        new(PublicEducation, "Catalog", "GET", "public/courses", null, "GetPublicCourses", typeof(GetPublicCoursesQuery)),
        new(PublicEducation, "HeadCatalog", "HEAD", "public/courses", null, "HeadPublicCourses", typeof(GetPublicCoursesQuery)),
        new(PublicEducation, "Course", "GET", "public/courses", "{slug}", "GetPublicCourse", typeof(GetPublicCourseQuery)),
        new(PublicEducation, "HeadCourse", "HEAD", "public/courses", "{slug}", "HeadPublicCourse", typeof(GetPublicCourseQuery)),
        new(PublicEducation, "Lesson", "GET", "public/courses", "{slug}/lessons/{lessonSlug}", "GetPublicLesson", typeof(GetPublicLessonQuery)),
        new(PublicEducation, "HeadLesson", "HEAD", "public/courses", "{slug}/lessons/{lessonSlug}", "HeadPublicLesson", typeof(GetPublicLessonQuery)),
        new(Account, "GetSite", "GET", "account", "site", "GetAccountSite", typeof(GetAccountSiteQuery))
    ];

    // These are mapping call sites, not a path-wide exception. A second health
    // mapping, even with the identical literal or inside a helper, is extra surface.
    private static readonly string[] InfrastructureMappings =
    [
        "Program.<Main>$ -> Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet",
        "Program.<Main>$ -> Microsoft.AspNetCore.Builder.ControllerEndpointRouteBuilderExtensions.MapControllers",
        Versioning + ".MapLearnStackOpenApi -> Microsoft.AspNetCore.Builder.OpenApiEndpointRouteBuilderExtensions.MapOpenApi",
        Versioning + ".MapLearnStackOpenApi -> Scalar.AspNetCore.ScalarEndpointRouteBuilderExtensions.MapScalarApiReference"
    ];

    /// <summary><see href="../../../docs/decisions/0064-tenant-api-authentication-boundary.md">ADR-0064</see> § Closed authenticated read.</summary>
    [Fact]
    public void Authenticated_Http_Surface_Is_Enumerated_And_ReadOnly() => WithProduction((modules, facts) =>
        Violations(modules, facts).Should().BeEmpty(
            "Fix: retain only the eight approved public GET/HEAD actions, exact AccountSite GET and enumerated infrastructure mappings; dispatch the exact Off read through the existing pipeline"));

    /// <summary><see href="../../../docs/decisions/0064-tenant-api-authentication-boundary.md">ADR-0064</see>'s controls mutate the same production route census.</summary>
    [Fact]
    public void Authenticated_http_census_detects_extra_routes_mutations_and_missing_subjects() => WithProduction((modules, facts) =>
    {
        Violations(modules, facts).Should().BeEmpty("the unmodified production census is the clean control");
        Violations(modules.Where(module => module.Assembly.Name.Name != "LearnStack.Api").ToArray(), facts)
            .Should().Contain(error => error.StartsWith("Scan every production assembly", StringComparison.Ordinal),
                "removing the production HTTP assembly must not produce a vacuous pass");
        Violations(modules, facts, _ => false).Should().Contain(error => error.StartsWith("Missing production controller:", StringComparison.Ordinal),
            "a weakened controller classifier loses required real roots");
        Violations(modules, facts, mappingPredicate: _ => false).Should().Contain(error => error.StartsWith("Require exactly one infrastructure mapping:", StringComparison.Ordinal),
            "a weakened map classifier loses real infrastructure mappings");

        var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
        var map = Types(modules).ToDictionary(Key, StringComparer.Ordinal);
        AssertPublicAuthorizationControls(map);
        AssertGlobalFilterControls(map);
        var account = api.GetType(Account);
        var action = account.Methods.Single(method => method.Name == "GetSite");
        AuthenticationMetadata.WithCompiledRole(api, metadata =>
        {
            action.CustomAttributes.Add(metadata);
            try
            {
                Dirty("Unexpected authorization metadata:", "a derived role attribute changes the actual AccountSite authorization policy");
                Action blinded = () => Violations(modules, facts, authorizationPredicate: type => type.FullName == typeof(AuthorizeAttribute).FullName)
                    .Should().Contain(error => error.StartsWith("Unexpected authorization metadata:", StringComparison.Ordinal),
                        "concrete-only metadata matching must fail this same compiled control");
                blinded.Should().Throw<Xunit.Sdk.XunitException>();
            }
            finally { action.CustomAttributes.Remove(metadata); }
        });
        var route = account.CustomAttributes.Single(attribute => attribute.AttributeType.FullName == Route);
        var resource = route.ConstructorArguments[0];
        route.ConstructorArguments[0] = new CustomAttributeArgument(api.TypeSystem.String, "account-extra");
        Dirty("Unexpected method/route/operation:", "a changed resource route is outside the exact account/site contract");
        route.ConstructorArguments[0] = resource;

        var http = action.CustomAttributes.Single(attribute => attribute.AttributeType.FullName == typeof(HttpGetAttribute).FullName);
        action.CustomAttributes.Remove(http);
        action.CustomAttributes.Add(Attribute(api, typeof(HttpPostAttribute), "site"));
        Dirty("Unexpected method/route/operation:", "a mutation verb cannot inherit AccountSite's approval");
        action.CustomAttributes.RemoveAt(action.CustomAttributes.Count - 1);
        action.CustomAttributes.Add(http);

        var anonymous = Attribute(api, typeof(AllowAnonymousAttribute));
        action.CustomAttributes.Add(anonymous);
        Dirty("Unexpected authorization metadata:", "action metadata must not bypass AccountSite's explicit authorization");
        action.CustomAttributes.Remove(anonymous);

        account.Methods.Remove(action);
        Dirty("Missing production action:", "an approved action missing from the production census is a failure");
        account.Methods.Add(action);

        var extraAction = new MethodDefinition("UndeclaredRead", MethodAttributes.Public, api.TypeSystem.Void);
        account.Methods.Add(extraAction);
        Dirty("Unapproved or overloaded action:", "MVC treats an undecorated public instance method as an action too");
        account.Methods.Remove(extraAction);

        foreach (var owner in modules.Where(module => module.Assembly.Name.Name is
                     "LearnStack.Api" or "LearnStack.Modules.Education.Application.Contracts" or "LearnStack.Infrastructure"))
        {
            var extra = new TypeDefinition("SurfaceProbe", "ExtraController", TypeAttributes.Public | TypeAttributes.Sealed,
                owner.ImportReference(typeof(ControllerBase)));
            owner.Types.Add(extra);
            Dirty("Unapproved production controller:", "controller discovery must cover every production assembly: " + owner.Name);
            owner.Types.Remove(extra);
        }

        var sendBody = ActionBodies(account, "GetSite").Single(method => method.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference target && target.DeclaringType.FullName == typeof(ISender).FullName && target.Name == "Send"));
        var construction = sendBody.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference target && target.DeclaringType.FullName == typeof(GetAccountSiteQuery).FullName);
        var originalConstructor = construction.Operand;
        construction.Operand = api.ImportReference(typeof(GetPublicSiteQuery).GetConstructors().Single());
        Dirty("Construct exactly ", "another approved public request cannot replace the exact authenticated request");
        var contracts = modules.Single(module => module.Assembly.Name.Name == typeof(GetAccountSiteQuery).Assembly.GetName().Name);
        var unregistered = new TypeDefinition("SurfaceProbe", "UnregisteredQuery", TypeAttributes.Public | TypeAttributes.Sealed,
            contracts.TypeSystem.Object);
        unregistered.Interfaces.Add(new InterfaceImplementation(contracts.ImportReference(typeof(IRequest<string>))));
        var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            contracts.TypeSystem.Void);
        unregistered.Methods.Add(constructor);
        contracts.Types.Add(unregistered);
        construction.Operand = api.ImportReference(constructor);
        Dirty("Construct exactly ", "a planted unregistered production request cannot enter the HTTP pipeline");
        construction.Operand = originalConstructor;
        contracts.Types.Remove(unregistered);

        var maps = MappingSites(modules).ToArray();
        EndpointCompositionControls.Plant(api, (prefix, because) => Dirty(prefix, because));
        var health = maps.Single(site => site.Target.Name == "MapGet");
        var duplicate = Instruction.Create(OpCodes.Call, health.Target);
        health.Owner.Body.Instructions.Add(duplicate);
        Dirty("Require exactly one infrastructure mapping:", "a second MapGet cannot borrow the same /healthz exception");
        health.Owner.Body.Instructions.Remove(duplicate);
        var helper = new MethodDefinition("HiddenHealthAlias", MethodAttributes.Private | MethodAttributes.Static, api.TypeSystem.Void);
        account.Methods.Add(helper);
        helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "/healthz"));
        helper.Body.Instructions.Add(duplicate);
        Dirty("Unapproved endpoint mapping:", "the infrastructure exclusion belongs to its exact production owner, not a path string");
        account.Methods.Remove(helper);

        var callback = health.Owner.Body.Instructions.TakeWhile(instruction => instruction != health.Instruction)
            .Where(instruction => instruction.OpCode == OpCodes.Ldftn).Select(instruction => (MethodReference)instruction.Operand).Last();
        var callbackBody = Types(modules).Single(type => Key(type) == Key(callback.DeclaringType)).Methods
            .Single(method => method.FullName == callback.FullName);
        foreach (var opcode in new[] { OpCodes.Ldsfld, OpCodes.Ldfld, OpCodes.Ldsflda, OpCodes.Ldflda })
        {
            var field = new FieldDefinition("HiddenHealthData", FieldAttributes.Private | FieldAttributes.Static, api.TypeSystem.String);
            callbackBody.DeclaringType.Fields.Add(field);
            var read = Instruction.Create(opcode, field);
            callbackBody.Body.Instructions.Add(read);
            Dirty("Unexpected infrastructure path/handler/signature:", "the data-free health callback cannot read hidden fields: " + opcode.Name);
            callbackBody.Body.Instructions.Remove(read);
            callbackBody.DeclaringType.Fields.Remove(field);
        }

        foreach (var mapping in maps.Where(site => site.Target.Name is "MapGet" or "MapOpenApi" or "MapScalarApiReference"))
        {
            var literal = mapping.Owner.Body.Instructions.TakeWhile(instruction => instruction != mapping.Instruction)
                .Last(instruction => instruction.OpCode == OpCodes.Ldstr);
            var original = literal.Operand;
            literal.Operand = "/extra-infrastructure";
            Dirty("Unexpected infrastructure path/handler/signature:", "an approved mapping cannot serve a different infrastructure path: " + mapping.Target.Name);
            literal.Operand = original;
        }

        Violations(modules, facts).Should().BeEmpty("restoring the actual production metadata restores the clean verdict");
        void Dirty(string prefix, string because) => Violations(modules, facts).Should()
            .Contain(error => error.StartsWith(prefix, StringComparison.Ordinal), because);
    });

    /// <summary><see href="../../../docs/decisions/0064-tenant-api-authentication-boundary.md">ADR-0064</see>'s controls plant capabilities in real production roots.</summary>
    [Fact]
    public void Authenticated_http_census_follows_production_helpers_and_nested_dispatch() => WithProduction((modules, facts) =>
    {
        var map = Types(modules).ToDictionary(Key, StringComparer.Ordinal);
        var account = map.Values.Single(type => type.FullName == Account);
        var handlers = HandlerRoots(map).ToArray();
        handlers.Should().HaveCount(5, "the four public queries and authenticated query have real production handlers");
        Violations(modules, facts).Should().BeEmpty();

        var publicSite = map.Values.Single(type => type.FullName == PublicSite);
        PublicSurfaceTests.PersistenceOffenders([publicSite], map).Should().BeEmpty(
            "a shared abstract controller base does not acquire the sibling AccountController");
        var collaborator = new FieldDefinition("AbstractCollaborator", FieldAttributes.Private, publicSite.BaseType);
        publicSite.Fields.Add(collaborator);
        PublicSurfaceTests.PersistenceOffenders([publicSite], map).Should().NotBeEmpty(
            "explicit injection of that same abstract base must still discover its concrete capabilities");
        publicSite.Fields.Remove(collaborator);

        foreach (var owner in new[] { account }.Concat(handlers))
        {
            foreach (var helperModule in modules.Where(module => module.Assembly.Name.Name is
                         "LearnStack.Modules.Education.Application.Contracts" or "LearnStack.Infrastructure"))
            {
                var helper = new TypeDefinition("SurfaceProbe", "ReadHelper", TypeAttributes.Public | TypeAttributes.Sealed,
                    helperModule.TypeSystem.Object);
                helperModule.Types.Add(helper);
                var edge = new FieldDefinition("ReadHelper", FieldAttributes.Private, owner.Module.ImportReference(helper));
                owner.Fields.Add(edge);
                Violations(modules, facts).Should().BeEmpty("a data-free production helper is a clean control");

                var dispatch = new FieldDefinition("Sender", FieldAttributes.Private, helperModule.ImportReference(typeof(ISender)));
                helper.Fields.Add(dispatch);
                Dirty(owner == account ? "HTTP persistence/handler/helper bypass:" : "Nested HTTP dispatch/helper handler:", "a hidden nested sender must be followed from " + owner.FullName + " into " + helperModule.Name);
                helper.Fields.Remove(dispatch);

                if (owner == account)
                {
                    PublicSurfaceTests.Dispatches(account, "GetSite", typeof(GetAccountSiteQuery).FullName).Should().BeTrue(
                        "the independent transitive guard checks helpers after a clean direct request construction");
                    helper.Fields.Add(new FieldDefinition("Database", FieldAttributes.Private,
                        helperModule.ImportReference(typeof(Npgsql.NpgsqlDataSource))));
                    Dirty("HTTP persistence/handler/helper bypass:", "controller helpers cannot acquire persistence before or after a sanctioned direct Send");
                    helper.Fields.Clear();
                }

                owner.Fields.Remove(edge);
                helperModule.Types.Remove(helper);
            }
        }

        var body = ActionBodies(account, "GetSite").Single(method => method.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference target && target.DeclaringType.FullName == typeof(ISender).FullName && target.Name == "Send"));
        var send = body.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Single(target => target.DeclaringType.FullName == typeof(ISender).FullName && target.Name == "Send");
        foreach (var owner in handlers)
        {
            var method = owner.Methods.Single(candidate => candidate.Name == "Handle");
            var planted = Instruction.Create(OpCodes.Callvirt, owner.Module.ImportReference(send));
            method.Body.Instructions.Add(planted);
            Dirty("Nested HTTP dispatch/helper handler:", "nested dispatch in the actual handler is outside the exact HTTP request set: " + owner.FullName);
            method.Body.Instructions.Remove(planted);
        }

        // Each approved handler is a root, not a collaborator another root may
        // call directly. Exercise interface and concrete signatures, locals and
        // actual Handle operands independently of the ISender controls above.
        foreach (var owner in handlers)
        {
            var target = handlers.Single(candidate => Handles(candidate, typeof(GetAccountSiteQuery).FullName!));
            foreach (var capability in new TypeReference[]
                     { owner.Module.ImportReference(target.Interfaces.Single().InterfaceType), owner.Module.ImportReference(target) })
            {
                var field = new FieldDefinition("DirectHandler", FieldAttributes.Private, capability);
                owner.Fields.Add(field);
                Dirty("Nested HTTP dispatch/helper handler:", "direct handler injection must not bypass the pipeline: " + capability.FullName);
                owner.Fields.Remove(field);

                var constructor = owner.Methods.Single(method => method.IsConstructor && !method.IsStatic);
                var parameter = new ParameterDefinition("directHandler", ParameterAttributes.None, capability);
                constructor.Parameters.Add(parameter);
                Dirty("Nested HTTP dispatch/helper handler:", "constructor-only handler injection remains a dispatch capability");
                constructor.Parameters.Remove(parameter);

                var method = owner.Methods.Single(candidate => candidate.Name == "Handle");
                var local = new VariableDefinition(capability);
                method.Body.Variables.Add(local);
                Dirty("Nested HTTP dispatch/helper handler:", "a handler local must not evade the field/constructor census");
                method.Body.Variables.Remove(local);

                var handle = new MethodReference("Handle", target.Methods.Single(candidate => candidate.Name == "Handle").ReturnType, capability)
                { HasThis = true };
                var direct = Instruction.Create(OpCodes.Callvirt, handle);
                method.Body.Instructions.Add(direct);
                Dirty("Nested HTTP dispatch/helper handler:", "direct interface/concrete Handle calls must be rejected");
                method.Body.Instructions.Remove(direct);
            }
        }

        var nested = new TypeDefinition("", "NestedReadHelper", TypeAttributes.NestedPrivate | TypeAttributes.Sealed, account.Module.TypeSystem.Object);
        account.NestedTypes.Add(nested);
        var nestedEdge = new FieldDefinition("NestedHelper", FieldAttributes.Private, nested);
        account.Fields.Add(nestedEdge);
        Violations(modules, facts).Should().BeEmpty("an ordinary nested data-free helper is legal");
        nested.Fields.Add(new FieldDefinition("Sender", FieldAttributes.Private, account.Module.ImportReference(typeof(ISender))));
        Dirty("HTTP persistence/handler/helper bypass:", "non-generated nested helpers must not fall outside the transitive scan");
        account.Fields.Remove(nestedEdge);
        account.NestedTypes.Remove(nested);
        AssertRootDispatchControls(map);
        Violations(modules, facts).Should().BeEmpty("all planted production capabilities have been removed");

        void Dirty(string prefix, string because) => Violations(modules, facts).Should()
            .Contain(error => error.StartsWith(prefix, StringComparison.Ordinal), because);
    });

    private static SortedSet<string> Violations(ModuleDefinition[] modules, CensusFacts facts,
        Func<TypeDefinition, bool>? controllerPredicate = null, Func<MethodReference, bool>? mappingPredicate = null,
        Func<TypeReference, bool>? authorizationPredicate = null)
    {
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        if (!modules.Select(module => module.Assembly.Name.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(facts.AssemblyNames, StringComparer.Ordinal))
            errors.Add("Scan every production assembly from backend/src; do not filter the HTTP/helper census.");
        var map = Types(modules).ToDictionary(Key, StringComparer.Ordinal);
        var controllers = map.Values.Where(controllerPredicate ?? (type => IsController(type, map))).ToArray();
        var approved = Endpoints.Select(endpoint => endpoint.Controller).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in approved.Except(controllers.Select(type => type.FullName), StringComparer.Ordinal))
            errors.Add("Missing production controller: " + name);
        foreach (var type in controllers)
        {
            var expected = Endpoints.Where(endpoint => endpoint.Controller == type.FullName).ToArray();
            if (expected.Length == 0) errors.Add("Unapproved production controller: " + type.FullName);
            var actions = ControllerMethods(type, map).Where(IsAction).ToArray();
            foreach (var name in expected.Select(endpoint => endpoint.Action).Except(actions.Select(method => method.Name), StringComparer.Ordinal))
                errors.Add("Missing production action: " + type.FullName + "." + name);
            foreach (var action in actions)
            {
                var entry = expected.SingleOrDefault(endpoint => endpoint.Action == action.Name);
                if (entry is null || actions.Count(candidate => candidate.Name == action.Name) != 1)
                {
                    errors.Add("Unapproved or overloaded action: " + type.FullName + "." + action.Name);
                    continue;
                }
                var resource = type.CustomAttributes.Where(attribute => attribute.AttributeType.FullName == Route).ToArray();
                var selectors = action.CustomAttributes.Where(attribute => IsRouteAttribute(attribute.AttributeType, map)).ToArray();
                if (resource.Length != 1 || StringArgument(resource[0]) != entry.Resource
                    || selectors.Length != 1
                    || selectors[0].AttributeType.FullName != "Microsoft.AspNetCore.Mvc.Http" + (entry.Verb == "GET" ? "Get" : "Head") + "Attribute"
                    || StringArgument(selectors[0]) != entry.Suffix
                    || selectors[0].Properties.SingleOrDefault(property => property.Name == "Name").Argument.Value as string != entry.Operation)
                    errors.Add("Unexpected method/route/operation: " + type.FullName + "." + action.Name);
                if (Attributes(type, map).Concat(action.CustomAttributes).Any(attribute => attribute.AttributeType.FullName == "LearnStack.Api.Versioning.ApiVersionAttribute"
                        && attribute.ConstructorArguments[0].Value is not 1))
                    errors.Add("HTTP ceiling permits only the existing /api/v1 major: " + type.FullName);
                if (!Authorized(type, action, map, authorizationPredicate)) errors.Add("Unexpected authorization metadata: " + type.FullName + "." + action.Name);
                if (!PublicSurfaceTests.Dispatches(type, action.Name, entry.Request.FullName))
                    errors.Add("Construct exactly " + entry.Request.FullName + " at the single direct ISender.Send: " + type.FullName + "." + action.Name);
            }
        }
        foreach (var offender in PublicSurfaceTests.PersistenceOffenders(controllers, map))
            errors.Add("HTTP persistence/handler/helper bypass: " + offender);

        var requests = Endpoints.Select(endpoint => endpoint.Request).Distinct().ToArray();
        var catalogue = facts.Catalogue;
        foreach (var request in requests)
        {
            if (!catalogue.TryGet(request, out var registration) || !registration.WritesNoRow || registration.Entries.Count != 0)
                errors.Add("HTTP request must be explicitly Off: " + request.FullName);
            if (request.IsDefined(typeof(PublicSurfaceAttribute), false) != (request != typeof(GetAccountSiteQuery)))
                errors.Add("Only the existing four public queries carry PublicSurface: " + request.FullName);
        }
        var handlers = HandlerRoots(map).ToArray();
        foreach (var request in requests)
            if (handlers.Count(type => Handles(type, request.FullName!)) != 1)
                errors.Add("Require exactly one production handler for " + request.FullName);
        foreach (var offender in NestedDispatchOffenders(handlers, map)) errors.Add(offender);
        foreach (var offender in GlobalFilterOffenders(map)) errors.Add(offender);

        var sites = MappingSites(modules, mappingPredicate).ToArray();
        var actualMappings = sites.Select(site => MappingName(site.Owner, site.Target)).ToArray();
        foreach (var mapping in InfrastructureMappings)
            if (actualMappings.Count(actual => actual == mapping) != 1) errors.Add("Require exactly one infrastructure mapping: " + mapping);
        foreach (var mapping in actualMappings.Except(InfrastructureMappings, StringComparer.Ordinal))
            errors.Add("Unapproved endpoint mapping: " + mapping);
        foreach (var site in sites)
            if (!InfrastructurePathIsApproved(site, map)) errors.Add("Unexpected infrastructure path/handler/signature: " + MappingName(site.Owner, site.Target));
        return errors;
    }

    private static bool Authorized(TypeDefinition controller, MethodDefinition action, Dictionary<string, TypeDefinition> map,
        Func<TypeReference, bool>? predicate)
    {
        var attributes = Attributes(controller, map).Concat(action.CustomAttributes).ToArray();
        var authorizations = attributes.Where(attribute => (predicate ?? (type => AuthenticationMetadata.IsData(type, map)))(attribute.AttributeType)).ToArray();
        var anonymous = attributes.Count(attribute => attribute.AttributeType.FullName == typeof(AllowAnonymousAttribute).FullName);
        if (controller.FullName != Account) return anonymous == 1 && authorizations.Length == 0;
        return anonymous == 0 && authorizations.Length == 1 && authorizations[0].ConstructorArguments.Count == 0
            && authorizations[0].AttributeType.FullName == typeof(AuthorizeAttribute).FullName
            && authorizations[0].Properties.Count == 1
            && authorizations[0].Properties[0].Name == nameof(AuthorizeAttribute.AuthenticationSchemes)
            && authorizations[0].Properties[0].Argument.Value as string == "TenantBearer";
    }

    private static IEnumerable<string> NestedDispatchOffenders(TypeDefinition[] roots, Dictionary<string, TypeDefinition> map)
    {
        var rootKeys = roots.Select(Key).ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<(string Root, string Type)>();
        var referencesByType = new Dictionary<string, TypeReference[]>(StringComparer.Ordinal);
        var implementationsByType = new Dictionary<string, TypeDefinition[]>(StringComparer.Ordinal);
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal);
        var queue = new Queue<(TypeDefinition Root, TypeDefinition Type)>(roots.Select(root => (root, root)));
        while (queue.TryDequeue(out var current))
        {
            var (root, type) = current;
            var typeKey = Key(type);
            if (!visited.Add((Key(root), typeKey))) continue;
            if (typeKey != Key(root) && rootKeys.Contains(typeKey))
            {
                yield return "Nested HTTP dispatch/helper handler: " + root.FullName + " -> " + type.FullName;
                continue;
            }
            if (type.IsInterface || type.IsAbstract)
            {
                if (!implementationsByType.TryGetValue(typeKey, out var implementations))
                    implementationsByType[typeKey] = implementations = map.Values.Where(candidate => !candidate.IsInterface
                        && PublicSurfaceTests.Implements(candidate, typeKey, map, [])).ToArray();
                foreach (var implementation in implementations) queue.Enqueue((root, implementation));
            }
            if (!referencesByType.TryGetValue(typeKey, out var references))
                referencesByType[typeKey] = references = Il.ReferencedTypeReferences(type).ToArray();
            if (!capabilities.TryGetValue(typeKey, out var capability))
                capabilities[typeKey] = capability = HasHandlerCapability(type, map);
            if (references.Any(reference => reference.FullName is "MediatR.ISender" or "MediatR.IMediator" or "MediatR.Mediator" or "MediatR.IPublisher")
                || capability
                || (!rootKeys.Contains(typeKey) && type.Interfaces.Any(contract => contract.InterfaceType.GetElementType().FullName == "MediatR.IRequestHandler`2")))
                yield return "Nested HTTP dispatch/helper handler: " + type.FullName;
            foreach (var reference in references)
                if (map.TryGetValue(Key(reference), out var helper)
                    && !(type.IsValueType && reference.GetElementType().FullName == "LearnStack.SharedKernel.Identifiers.IStronglyTypedId`1"
                        && type.Interfaces.Any(contract => Key(contract.InterfaceType) == Key(reference)))) queue.Enqueue((root, helper));
        }
    }

    private static bool HasHandlerCapability(TypeDefinition type, Dictionary<string, TypeDefinition> map)
    {
        bool Handler(TypeReference reference) => Il.ExpandedTypeReferences(reference).Any(candidate =>
            candidate.FullName is "MediatR.IRequestHandler`1" or "MediatR.IRequestHandler`2"
            || map.TryGetValue(Key(candidate), out var definition) && definition.Interfaces.Any(contract =>
                contract.InterfaceType.GetElementType().FullName is "MediatR.IRequestHandler`1" or "MediatR.IRequestHandler`2"));

        static bool CapturedOwner(TypeReference reference, MethodDefinition method)
        {
            for (var generated = method.DeclaringType; generated is not null && generated.Name.Contains('<', StringComparison.Ordinal);
                 generated = generated.DeclaringType)
                if (generated.DeclaringType is { } owner && Key(reference) == Key(owner)) return true;
            return false;
        }

        return type.Fields.Any(field => field.Name != "<>4__this" && Handler(field.FieldType))
            || Il.Methods(type).Any(pair => Handler(pair.Definition.ReturnType)
                || pair.Definition.Parameters.Any(parameter => Handler(parameter.ParameterType))
                || pair.Definition.HasBody && (pair.Definition.Body.Variables.Any(variable => Handler(variable.VariableType) && !CapturedOwner(variable.VariableType, pair.Definition))
                    || pair.Definition.Body.Instructions.Any(instruction => instruction.Operand switch
                    {
                        TypeReference reference => Handler(reference),
                        // Async state machines capture their own handler as this;
                        // that generated back-reference is not nested dispatch.
                        FieldReference field => field.Name != "<>4__this" && Handler(field.FieldType),
                        MethodReference target => (target.Name is "Handle" or ".ctor" && Handler(target.DeclaringType))
                            || Handler(target.ReturnType) || target.Parameters.Any(parameter => Handler(parameter.ParameterType))
                            || target is GenericInstanceMethod generic && generic.GenericArguments.Any(Handler),
                        _ => false
                    })));
    }

    private static IEnumerable<TypeDefinition> HandlerRoots(Dictionary<string, TypeDefinition> map) => map.Values.Where(type =>
        !type.IsAbstract && !type.IsInterface && Endpoints.Any(endpoint => Handles(type, endpoint.Request.FullName!)));

    private static bool Handles(TypeDefinition type, string request) => type.Interfaces.Any(contract =>
        contract.InterfaceType is GenericInstanceType generic && generic.ElementType.FullName == "MediatR.IRequestHandler`2"
        && generic.GenericArguments[0].FullName == request);

    private static bool IsController(TypeDefinition type, Dictionary<string, TypeDefinition> map) =>
        !type.IsAbstract && !type.IsInterface && (type.IsPublic || type.IsNestedPublic) && !type.HasGenericParameters
        && !Attributes(type, map).Any(attribute => attribute.AttributeType.FullName == typeof(NonControllerAttribute).FullName)
        && (type.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            || Attributes(type, map).Any(attribute => attribute.AttributeType.FullName is
                "Microsoft.AspNetCore.Mvc.ControllerAttribute" or "Microsoft.AspNetCore.Mvc.ApiControllerAttribute")
            || Bases(type, map).Any(parent => parent.FullName == typeof(ControllerBase).FullName));

    private static bool IsAction(MethodDefinition method) => method.IsPublic && !method.IsStatic && !method.IsConstructor
        && !method.IsSpecialName && !method.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == typeof(NonActionAttribute).FullName);

    private static IEnumerable<MethodDefinition> ControllerMethods(TypeDefinition type, Dictionary<string, TypeDefinition> map) =>
        new[] { type }.Concat(Bases(type, map).OfType<TypeDefinition>()).SelectMany(owner => owner.Methods);

    private static IEnumerable<CustomAttribute> Attributes(TypeDefinition type, Dictionary<string, TypeDefinition> map) =>
        new[] { type }.Concat(Bases(type, map).OfType<TypeDefinition>()).SelectMany(owner => owner.CustomAttributes);

    private static IEnumerable<TypeReference> Bases(TypeDefinition type, Dictionary<string, TypeDefinition> map)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var reference = type.BaseType; reference is not null && visited.Add(Key(reference));)
        {
            if (!map.TryGetValue(Key(reference), out var parent)) { yield return reference; yield break; }
            yield return parent;
            reference = parent.BaseType;
        }
    }

    private static bool IsRouteAttribute(TypeReference attribute, Dictionary<string, TypeDefinition> map) =>
        attribute.FullName == Route || attribute.FullName == typeof(AcceptVerbsAttribute).FullName
        || (attribute.Namespace == "Microsoft.AspNetCore.Mvc" && attribute.Name.StartsWith("Http", StringComparison.Ordinal))
        || map.TryGetValue(Key(attribute), out var definition) && (Bases(definition, map).Any(parent => IsRouteAttribute(parent, map))
            || definition.Interfaces.Any(contract => contract.InterfaceType.FullName is
                "Microsoft.AspNetCore.Mvc.Routing.IRouteTemplateProvider" or "Microsoft.AspNetCore.Mvc.Routing.IActionHttpMethodProvider"));

    private static string? StringArgument(CustomAttribute attribute) => attribute.ConstructorArguments.Count == 0
        ? null : attribute.ConstructorArguments[0].Value as string;

    private sealed record Mapping(MethodDefinition Owner, Instruction Instruction, MethodReference Target);

    private static IEnumerable<Mapping> MappingSites(IEnumerable<ModuleDefinition> modules, Func<MethodReference, bool>? predicate = null) =>
        Types(modules).SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                    && (predicate ?? IsEndpointMapping)(target))
                .Select(instruction => new Mapping(method, instruction, (MethodReference)instruction.Operand)));

    private static bool IsEndpointMapping(MethodReference target) => EndpointComposition.IsCapability(target);

    private static string MappingName(MethodDefinition owner, MethodReference target) =>
        owner.DeclaringType.FullName + "." + owner.Name + " -> " + target.DeclaringType.FullName + "." + target.Name;

    private static bool InfrastructurePathIsApproved(Mapping mapping, Dictionary<string, TypeDefinition> map)
    {
        var target = mapping.Target;
        if (mapping.Instruction.OpCode != OpCodes.Call || target.Parameters.Count == 0
            || target.Parameters[0].ParameterType.FullName != "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder") return false;
        return target.Name switch
        {
            "MapControllers" => target.Parameters.Count == 1,
            "MapGet" => target.Parameters.Count == 3 && HealthIsDataFree(mapping.Owner, mapping.Instruction, map),
            "MapOpenApi" => target.Parameters.Count == 2
                && EndpointComposition.ArgumentProducer(mapping.Owner, mapping.Instruction, 1) is { } path
                && path.Operand is MethodReference { Name: "Concat", DeclaringType.FullName: "System.String", Parameters.Count: 3 }
                && EndpointComposition.HasLiteralArgument(mapping.Owner, path, 0, "/openapi/{documentName:regex(^(")
                && EndpointComposition.HasLiteralArgument(mapping.Owner, path, 2, ")$)}.json"),
            "MapScalarApiReference" => target.Parameters.Count == 3 && EndpointComposition.HasLiteralPath(mapping.Owner, mapping.Instruction, "/docs"),
            _ => false
        };
    }

    private static bool HealthIsDataFree(MethodDefinition owner, Instruction mapping, Dictionary<string, TypeDefinition> map)
    {
        if (!EndpointComposition.HasLiteralPath(owner, mapping, "/healthz")) return false;
        var before = owner.Body.Instructions.TakeWhile(instruction => instruction != mapping).ToArray();
        var path = Array.IndexOf(before, EndpointComposition.ArgumentProducer(owner, mapping, 1));
        var callbacks = before.Skip(path).Where(instruction => instruction.OpCode == OpCodes.Ldftn)
            .Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
        if (callbacks.Length != 1 || !map.TryGetValue(Key(callbacks[0].DeclaringType), out var type)) return false;
        var callback = type.Methods.SingleOrDefault(method => method.FullName == callbacks[0].FullName);
        return callback is { HasBody: true } && callback.Parameters.Count == 0
            && !callback.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Ldfld || instruction.OpCode == OpCodes.Ldsfld
                || instruction.OpCode == OpCodes.Ldflda || instruction.OpCode == OpCodes.Ldsflda)
            && callback.Body.Instructions.Any(instruction => instruction.Operand as string == "healthy")
            && callback.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().All(target =>
                target.Name == ".ctor" && target.DeclaringType.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                || target.DeclaringType.FullName == "Microsoft.AspNetCore.Http.Results" && target.Name == "Ok");
    }

    private static IEnumerable<MethodDefinition> ActionBodies(TypeDefinition type, string action) => Il.Methods(type)
        .Where(method => method.Definition.HasBody && (method.DeclaredAs == action
            || method.Definition.Name.StartsWith("<" + action + ">", StringComparison.Ordinal)))
        .Select(method => method.Definition);

    private static CustomAttribute Attribute(ModuleDefinition module, Type type, string? value = null)
    {
        var result = new CustomAttribute(module.ImportReference(type.GetConstructor(value is null ? Type.EmptyTypes : [typeof(string)])!));
        if (value is not null) result.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, value));
        return result;
    }

    private static string Key(TypeReference type) =>
        type.GetElementType().Scope.Name.Replace(".dll", "", StringComparison.Ordinal) + ":" + type.GetElementType().FullName;

    private static IEnumerable<TypeDefinition> Types(IEnumerable<ModuleDefinition> modules) => modules.SelectMany(module =>
        module.Types.SelectMany(Flatten));

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));

    private sealed record CensusFacts(string[] AssemblyNames, AuditCatalog Catalogue);

    private static void WithProduction(Action<ModuleDefinition[], CensusFacts> assertion)
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try { assertion(modules, new CensusFacts(ProductionAssemblies.Names().ToArray(), AuditCatalogDiscovery.Catalogue())); }
        finally { foreach (var module in modules) module.Dispose(); }
    }

}
