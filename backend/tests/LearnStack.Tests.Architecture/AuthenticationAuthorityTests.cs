using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Identifiers;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>ADR-0064: raw identity-provider claims never become a second application authority.</summary>
public sealed partial class AuthenticationAuthorityTests
{
    private const string Authentication = "LearnStack.Api.Authentication.";
    private const string Identity = Authentication + nameof(ValidatedTenantIdentity);
    private const string Feature = Authentication + nameof(TenantCredentialFeature);
    private const string Events = Authentication + nameof(TenantAuthenticationEvents);
    private const string Profile = Authentication + nameof(TenantTokenProfile);
    private const string Limiter = Authentication + nameof(TenantSubjectRateLimiter);
    private const string Resolver = "LearnStack.Api.Tenancy.TenantResolverMiddleware";
    private static readonly string[] IdentityProperties = ["ActorUserId", "Issuer", "OrganizationId", "Subject", "TenantId"];
    private static readonly Dictionary<string, Type> IdentityTypes = new(StringComparer.Ordinal)
    {
        ["Issuer"] = typeof(string),
        ["Subject"] = typeof(string),
        ["ActorUserId"] = typeof(UserId),
        ["TenantId"] = typeof(TenantId?),
        ["OrganizationId"] = typeof(OrganizationId?)
    };

    private sealed record Method(TypeDefinition Owner, string Name, MethodDefinition Definition)
    {
        internal string Site => Owner.FullName + "." + Name;
    }

    private sealed record Call(Method Source, MethodReference Target);

    /// <summary>ADR-0064 § Access-token profile and § Resolution and attribution.</summary>
    [Fact]
    public void Keycloak_Roles_Are_Not_Application_Authority() => WithProduction(modules =>
        Offenders(modules).Should().BeEmpty(
            "Fix: consume only the validated typed projection at its enumerated boundaries; Phase 03 owns application permissions, never realm roles or free claim text"));

    private static SortedSet<string> Offenders(ModuleDefinition[] modules,
        IEnumerable<Method>? planted = null, Func<MethodReference, bool>? rawReader = null)
    {
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        if (!modules.Select(module => module.Assembly.Name.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(ProductionAssemblies.Names(), StringComparer.Ordinal))
            errors.Add("Require every production assembly, including the real API trust boundary.");
        var types = modules.SelectMany(module => module.Types.SelectMany(Flatten)).ToArray();
        var methods = Methods(types).Concat(planted ?? []).ToArray();
        var calls = methods.SelectMany(method => method.Definition.Body.Instructions
            .Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Select(target => new Call(method, target))).ToArray();

        foreach (var call in calls.Where(call => (rawReader ?? IsRawAuthorityRead)(call.Target)))
            errors.Add("Raw claim/role authority: " + call.Source.Site + " -> " + call.Target.FullName);
        foreach (var type in types)
            foreach (var attribute in type.CustomAttributes.Concat(type.Methods.SelectMany(method => method.CustomAttributes)))
                if (attribute.AttributeType.FullName == "Microsoft.AspNetCore.Authorization.AuthorizeAttribute"
                    && attribute.Properties.Any(property => property.Name == "Roles"))
                    errors.Add("Realm-role authorization metadata: " + type.FullName);

        Require(Target(Identity, ".ctor", parameters: 5), Profile + ".TryReadValidated");
        Require(Target(Identity, ".ctor", parameters: 1), Identity + ".<Clone>$");
        Require(Target(Feature, ".ctor"), Events + ".MessageReceived");
        Require(Target(Feature, "set_Identity"), Events + ".AuthenticationFailed", Events + ".TokenValidated");
        Require(Target(Profile, "TryReadValidated"), Events + ".TokenValidated");
        Require(method => method.Name == "Set" && method is GenericInstanceMethod generic
                && generic.GenericArguments.Any(argument => Is(argument, Feature)), Events + ".MessageReceived");
        foreach (var call in calls.Where(call => Is(call.Target.DeclaringType, Identity)
                     && (call.Target.Name.StartsWith("set_", StringComparison.Ordinal) || call.Target.Name == "<Clone>$")
                 || Is(call.Target.DeclaringType, Events) && call.Target.Name == "TokenValidated"))
            errors.Add("Cannot forge, clone, mutate or directly invoke validated identity: " + call.Source.Site);

        // Issuer/sub may select a private accounting partition, never permission.
        // Scope/actor consumption remains explicit even inside the API assembly.
        Consumers("Issuer", Limiter + ".AttemptAcquire");
        Consumers("Subject", Limiter + ".AttemptAcquire");
        Consumers("ActorUserId", Resolver + ".BuildAttemptAsync", Resolver + ".RecordRefusalAsync",
            "LearnStack.Api.Tenancy.TenantAssertionMiddleware.InvokeAsync",
            Authentication + "TenantSurfaceAdmissionMiddleware.HasHostAuthority");
        Consumers("TenantId", Resolver + ".BuildAttemptAsync", Resolver + ".InvokeAsync",
            Authentication + "TenantSurfaceAdmissionMiddleware.HasHostAuthority");
        Consumers("OrganizationId", Resolver + ".BuildAttemptAsync");

        foreach (var method in methods)
            foreach (var instruction in method.Definition.Body.Instructions)
                if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                    && Is(field.DeclaringType, Feature) && Is(field.FieldType, Identity)
                    && method.Site != Feature + ".set_Identity")
                    errors.Add("Direct identity backing-field write: " + method.Site);

        var identities = types.Where(type => Is(type, Identity)).ToArray();
        var features = types.Where(type => Is(type, Feature)).ToArray();
        if (identities.Length != 1 || features.Length != 1)
            errors.Add("Missing unique production identity/feature shapes.");
        else
        {
            var identity = identities[0];
            if (!identity.IsSealed || identity.IsPublic
                || identity.Fields.Any(field => !field.IsStatic && !field.IsInitOnly)
                || !identity.Properties.Where(property => property.GetMethod?.IsPublic == true)
                    .Select(property => property.Name).Order(StringComparer.Ordinal)
                    .SequenceEqual(IdentityProperties, StringComparer.Ordinal)
                || identity.Properties.Where(property => property.GetMethod?.IsPublic == true).Any(property =>
                    property.SetMethod?.ReturnType is not RequiredModifierType modifier
                    || modifier.ModifierType.FullName != "System.Runtime.CompilerServices.IsExternalInit"
                    || !IdentityTypes.TryGetValue(property.Name, out var expected)
                    || !Matches(property.PropertyType, expected)
                    || !Matches(property.GetMethod.ReturnType, expected)
                    || property.SetMethod.Parameters.Count != 1
                    || !Matches(property.SetMethod.Parameters[0].ParameterType, expected)))
                errors.Add("Validated identity must retain its sealed internal immutable typed shape.");
            if (!features[0].IsSealed || features[0].IsPublic
                || features[0].Properties.SingleOrDefault(property => property.Name == "Identity") is not { } identityProperty
                || !Is(identityProperty.PropertyType, Identity))
                errors.Add("The request feature may carry only the closed typed identity.");
        }
        return errors;

        void Require(Func<MethodReference, bool> target, params string[] owners)
        {
            var actual = calls.Where(call => target(call.Target)).Select(call => call.Source.Site).Order(StringComparer.Ordinal);
            if (!actual.SequenceEqual(owners.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                errors.Add("Changed/missing trust capability callers: " + string.Join(", ", owners));
        }

        void Consumers(string property, params string[] owners)
        {
            var actual = calls.Where(call => Is(call.Target.DeclaringType, Identity) && call.Target.Name == "get_" + property
                    && call.Source.Owner.FullName != Identity)
                .Select(call => call.Source.Site).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            if (!actual.SequenceEqual(owners.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                errors.Add("Changed/missing validated " + property + " consumers: " + string.Join(", ", actual));
        }
    }

    private static bool IsRawAuthorityRead(MethodReference target) =>
        target.DeclaringType.Namespace is "System.Security.Claims" or "System.Security.Principal"
        || target.DeclaringType.FullName == "Microsoft.AspNetCore.Http.HttpContext" && target.Name is "get_User" or "set_User"
        || target.DeclaringType.FullName == "Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder"
            && target.Name is "RequireRole" or "RequireClaim"
        || target.DeclaringType.FullName is
            "Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement"
            or "Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement"
        || target.DeclaringType.FullName == "Microsoft.AspNetCore.Authorization.AuthorizeAttribute" && target.Name == "set_Roles";

    private static Func<MethodReference, bool> Target(string type, string name, int? parameters = null) =>
        method => Is(method.DeclaringType, type) && method.Name == name
            && (parameters is null || method.Parameters.Count == parameters);

    private static bool Is(TypeReference type, string name) => type.FullName == name
        && type.Scope.Name.Replace(".dll", "", StringComparison.Ordinal) == "LearnStack.Api";

    private static bool Matches(TypeReference actual, Type expected) => Nullable.GetUnderlyingType(expected) is { } underlying
        ? actual is GenericInstanceType generic && generic.ElementType.FullName == "System.Nullable`1"
            && generic.GenericArguments.Count == 1 && Matches(generic.GenericArguments[0], underlying)
        : actual.FullName == expected.FullName && (expected == typeof(string)
            || actual.Scope.Name.Replace(".dll", "", StringComparison.Ordinal) == expected.Assembly.GetName().Name);

    private static IEnumerable<Method> Methods(IEnumerable<TypeDefinition> types)
    {
        var visited = new HashSet<MethodDefinition>();
        foreach (var type in types)
            foreach (var (definition, declaredAs) in Il.Methods(type))
                if (definition.HasBody && visited.Add(definition)) yield return new Method(type, declaredAs, definition);
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));

    private static void WithProduction(Action<ModuleDefinition[]> assertion)
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try { assertion(modules); }
        finally { foreach (var module in modules) module.Dispose(); }
    }
}
