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
    private const string Verified = Authentication + nameof(VerifiedProviderSubject);
    private const string Feature = Authentication + nameof(TenantCredentialFeature);
    private const string Events = Authentication + nameof(TenantAuthenticationEvents);
    private const string Profile = Authentication + nameof(TenantTokenProfile);
    private const string Limiter = Authentication + nameof(TenantSubjectRateLimiter);
    private const string BindingAdmission = Authentication + nameof(TenantIdentityBindingAdmissionMiddleware);
    private const string BindingResolver = Authentication + nameof(TenantIdentityBindingResolver);
    private const string Provenance = Authentication + nameof(TenantSubjectAdmissionProvenance);
    private const string CandidateSource = Authentication + nameof(TenantAuthorizationCandidateSource);
    private const string TransportSource = Authentication + nameof(HttpAuthorizationTransportSource);
    private const string CredentialWitness = CandidateSource + "/CredentialWitness";
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
        internal string Site => Definition.DeclaringType == Owner ? Definition.FullName
            : Owner.Methods.SingleOrDefault(method => method.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                && attribute.ConstructorArguments.Any(argument => argument.Value is TypeReference stateMachine
                    && stateMachine.FullName == Definition.DeclaringType.FullName)))?.FullName ?? Definition.FullName;
    }

    private sealed record Call(Method Source, MethodReference Target);

    /// <summary>ADR-0064 § Access-token profile and § Resolution and attribution.</summary>
    [Fact]
    public void Keycloak_Roles_Are_Not_Application_Authority() => WithProduction(modules =>
        Offenders(modules).Should().BeEmpty(
            "Fix: consume only the validated typed projection at its enumerated boundaries; Phase 03 owns application permissions, never realm roles or free claim text"));

    private static SortedSet<string> Offenders(ModuleDefinition[] modules,
        IEnumerable<Method>? planted = null, Func<MethodReference, bool>? rawReader = null,
        Func<Method, bool>? projectionUse = null, Func<TypeReference, bool>? authorizationData = null)
    {
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        if (!modules.Select(module => module.Assembly.Name.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(ProductionAssemblies.Names(), StringComparer.Ordinal))
            errors.Add("Require every production assembly, including the real API trust boundary.");
        var types = modules.SelectMany(module => module.Types.SelectMany(Flatten)).ToArray();
        var typeMap = types.ToDictionary(AuthenticationMetadata.Key, StringComparer.Ordinal);
        authorizationData ??= reference => AuthenticationMetadata.IsData(reference, typeMap);
        var methods = Methods(types).Concat(planted ?? []).ToArray();
        var calls = methods.SelectMany(method => method.Definition.Body.Instructions
            .Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Select(target => new Call(method, target))).ToArray();

        foreach (var call in calls.Where(call => (rawReader ?? IsRawAuthorityRead)(call.Target)
                     && !(call.Source.Site == ExpectedSite(Events + ".TokenValidated")
                         && call.Target.FullName == "System.String Microsoft.IdentityModel.JsonWebTokens.JsonWebToken::get_EncodedToken()")
                     && !IsClosedRolesAbsenceCheck(call)))
            errors.Add("Raw claim/role authority: " + call.Source.Site + " -> " + call.Target.FullName);
        // Object virtual dispatch and generic formatting can export a positional
        // record without an identity-declared call. Close acquisition, holders
        // and forwarding too, rather than treating getters as the only readers.
        foreach (var method in methods.Where(projectionUse ?? TouchesProjection))
            if (method.Owner.FullName != Identity && method.Owner.FullName != Verified && method.Owner.FullName != Feature && !ApprovedSites.Values.Contains(method.Site, StringComparer.Ordinal))
                errors.Add("Unapproved typed identity/feature consumer: " + method.Site);
        foreach (var method in methods.Where(method => method.Owner.FullName != Identity && method.Owner.FullName != Verified && method.Owner.FullName != Feature
                     && TouchesProjection(method) && ExportsProjection(method)))
            errors.Add("Opaque typed identity/feature export: " + method.Site);
        foreach (var type in types)
        {
            if (type.GenericParameters.Concat(type.Methods.SelectMany(method => method.GenericParameters))
                .SelectMany(parameter => parameter.Constraints).Any(constraint => CarriesProjection(constraint.ConstraintType)))
                errors.Add("Identity/feature generic constraint outside the closed projection: " + type.FullName);
            if (authorizationData(type)) errors.Add("Unapproved authorization metadata type: " + type.FullName);
            foreach (var attribute in type.CustomAttributes.Concat(type.Methods.SelectMany(method => method.CustomAttributes)))
                if (authorizationData(attribute.AttributeType)
                    && (attribute.AttributeType.FullName != "Microsoft.AspNetCore.Authorization.AuthorizeAttribute"
                        || attribute.Properties.Any(property => property.Name == "Roles")
                        || attribute.Fields.Any(field => field.Name == "Roles")))
                    errors.Add("Realm-role authorization metadata: " + type.FullName);
        }

        Require(method => method.FullName == IdentityConstructor, BindingAdmission + ".InvokeAsync");
        Require(method => method.FullName == IdentityCopyConstructor, Identity + ".<Clone>$");
        Require(method => method.FullName == VerifiedConstructor, Profile + ".TryReadValidated");
        Require(method => method.FullName == VerifiedCopyConstructor, Verified + ".<Clone>$");
        Require(Target(Feature, ".ctor"), Events + ".MessageReceived");
        Require(Target(Feature, "set_Identity"), Events + ".AuthenticationFailed",
            Authentication + "TenantCredentialAdmissionMiddleware.InvokeAsync",
            Authentication + "TenantSubjectAdmissionMiddleware.InvokeAsync", BindingAdmission + ".InvokeAsync",
            BindingAdmission + ".InvokeAsync", BindingAdmission + ".Refuse");
        Require(Target(Feature, "set_VerifiedSubject"), Events + ".AuthenticationFailed", Events + ".TokenValidated");
        Require(Target(Profile, "TryReadValidated"), Events + ".TokenValidated");
        Require(method => method.Name == "Set" && method is GenericInstanceMethod generic
                && generic.GenericArguments.Any(argument => Is(argument, Feature)), Events + ".MessageReceived");
        foreach (var call in calls.Where(call => (Is(call.Target.DeclaringType, Identity) || Is(call.Target.DeclaringType, Verified))
                     && (call.Target.Name.StartsWith("set_", StringComparison.Ordinal) || call.Target.Name is "<Clone>$" or "Deconstruct")
                 || Is(call.Target.DeclaringType, Events) && call.Target.Name == "TokenValidated"))
            errors.Add("Cannot forge, clone, mutate, deconstruct or directly invoke validated identity: " + call.Source.Site);

        // Issuer/sub may select a private accounting partition, never permission.
        // Scope/actor consumption remains explicit even inside the API assembly.
        Consumers("Issuer", CandidateSource + ".MatchesIdentity");
        Consumers("Subject", CandidateSource + ".MatchesIdentity");
        VerifiedConsumers("Issuer", Limiter + ".AttemptAcquire", BindingResolver + ".ResolveExistingAsync", BindingAdmission + ".InvokeAsync", CandidateSource + ".MatchesIdentity");
        VerifiedConsumers("Subject", Limiter + ".AttemptAcquire", BindingResolver + ".ResolveExistingAsync", BindingAdmission + ".InvokeAsync", CandidateSource + ".MatchesIdentity");
        VerifiedConsumers("ClaimedActor", BindingResolver + ".ResolveExistingAsync", CandidateSource + ".MatchesIdentity");
        VerifiedConsumers("TenantId", BindingAdmission + ".InvokeAsync", CandidateSource + ".MatchesIdentity");
        VerifiedConsumers("OrganizationId", BindingAdmission + ".InvokeAsync", CandidateSource + ".MatchesIdentity");
        Consumers("ActorUserId", Resolver + ".BuildAttemptAsync", Resolver + ".RecordRefusalAsync",
            "LearnStack.Api.Tenancy.TenantAssertionMiddleware.InvokeAsync",
            Authentication + "TenantSurfaceAdmissionMiddleware.HasHostAuthority", CandidateSource + ".ForValidatedScope", CandidateSource + ".MatchesIdentity",
            TransportSource + ".Capture");
        Consumers("TenantId", Resolver + ".BuildAttemptAsync", Resolver + ".InvokeAsync",
            Authentication + "TenantSurfaceAdmissionMiddleware.HasHostAuthority", CandidateSource + ".ForValidatedScope", CandidateSource + ".MatchesIdentity",
            TransportSource + ".Capture");
        Consumers("OrganizationId", Resolver + ".BuildAttemptAsync", Authentication + "TenantSurfaceAdmissionMiddleware.HasHostAuthority",
            CandidateSource + ".ForValidatedScope", CandidateSource + ".MatchesIdentity");

        foreach (var method in methods)
            foreach (var instruction in method.Definition.Body.Instructions)
                if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field
                    && Is(field.DeclaringType, Feature) && (Is(field.FieldType, Identity) || Is(field.FieldType, Verified))
                    && method.Site != ExpectedSite(Feature + (Is(field.FieldType, Identity) ? ".set_Identity" : ".set_VerifiedSubject")))
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
        if (identities.Length == 1 && !HasClosedIdentityShape(identities[0]))
            errors.Add("Validated identity has an unexpected member or constructor signature.");
        if (features.Length == 1 && !HasClosedFeatureShape(features[0]))
            errors.Add("Credential feature has an unexpected member or constructor signature.");
        var verified = types.Where(type => Is(type, Verified)).ToArray();
        if (verified.Length != 1 || !HasClosedVerifiedShape(verified[0]))
            errors.Add("Verified provider subject must retain its closed immutable typed shape.");
        var provenance = types.Where(type => Is(type, Provenance)).ToArray();
        if (provenance.Length != 1 || !HasClosedProvenanceShape(provenance[0]))
            errors.Add("Subject admission provenance must retain its closed immutable request-local shape.");
        var witnesses = types.Where(type => Is(type, CredentialWitness)).ToArray();
        if (witnesses.Length != 1 || !HasClosedCredentialWitnessShape(witnesses[0]))
            errors.Add("Candidate credential witness must retain its private immutable nonformatting shape.");
        return errors;

        void Require(Func<MethodReference, bool> target, params string[] owners)
        {
            var actual = calls.Where(call => target(call.Target)).Select(call => call.Source.Site).Order(StringComparer.Ordinal);
            if (!actual.SequenceEqual(owners.Select(ExpectedSite).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                errors.Add("Changed/missing trust capability callers: " + string.Join(", ", owners));
        }

        void Consumers(string property, params string[] owners)
            => ProjectionConsumers(Identity, property, owners);

        void VerifiedConsumers(string property, params string[] owners)
            => ProjectionConsumers(Verified, property, owners);

        void ProjectionConsumers(string projection, string property, params string[] owners)
        {
            var actual = calls.Where(call => Is(call.Target.DeclaringType, projection) && call.Target.Name == "get_" + property
                    && call.Source.Owner.FullName != projection)
                .Select(call => call.Source.Site).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            if (!actual.SequenceEqual(owners.Select(ExpectedSite).Order(StringComparer.Ordinal), StringComparer.Ordinal))
                errors.Add("Changed/missing validated " + property + " consumers: " + string.Join(", ", actual));
        }
    }

    private static bool IsRawAuthorityRead(MethodReference target) =>
        target.DeclaringType.Namespace is "System.Security.Claims" or "System.Security.Principal"
            or "Microsoft.IdentityModel.JsonWebTokens" or "System.IdentityModel.Tokens.Jwt"
        // Validation policy/configuration is infrastructure, but validation results
        // and SecurityToken members remain raw provider authority.
        || target.DeclaringType.Namespace == "Microsoft.IdentityModel.Tokens"
            && target.DeclaringType.Name is not ("TokenValidationParameters" or "BaseConfiguration" or "BaseConfigurationManager")
        || target.DeclaringType.FullName == "Microsoft.AspNetCore.Http.HttpContext" && target.Name is "get_User" or "set_User"
        || target.DeclaringType.FullName is "Microsoft.AspNetCore.Authentication.AuthenticateResult"
            or "Microsoft.AspNetCore.Authentication.AuthenticationTicket" && target.Name == "get_Principal"
        || target.DeclaringType.FullName is "Microsoft.AspNetCore.Authorization.IAuthorizationService"
            or "Microsoft.AspNetCore.Authorization.AuthorizationServiceExtensions"
            && target.Name == "AuthorizeAsync" && target.Parameters.Any(parameter =>
                parameter.ParameterType.FullName == "System.Security.Claims.ClaimsPrincipal")
        || target.DeclaringType.FullName == "Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder"
            && target.Name is "RequireRole" or "RequireClaim"
        || target.DeclaringType.FullName is
            "Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement"
            or "Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement"
        || target.DeclaringType.FullName is "Microsoft.AspNetCore.Authorization.AuthorizeAttribute"
            or "Microsoft.AspNetCore.Authorization.IAuthorizeData" && target.Name is "get_Roles" or "set_Roles";

    private static Func<MethodReference, bool> Target(string type, string name) =>
        method => Is(method.DeclaringType, type) && method.FullName == ExpectedSite(type + "." + name);

    private static bool Is(TypeReference type, string name) => type.FullName == name
        && type.Scope.Name.Replace(".dll", "", StringComparison.Ordinal) == "LearnStack.Api";

    private static bool Matches(TypeReference actual, Type expected) => Nullable.GetUnderlyingType(expected) is { } underlying
        ? actual is GenericInstanceType generic && generic.ElementType.FullName == "System.Nullable`1"
            && generic.GenericArguments.Count == 1 && Matches(generic.GenericArguments[0], underlying)
        : actual.FullName == expected.FullName && (expected == typeof(string)
            || actual.Scope.Name.Replace(".dll", "", StringComparison.Ordinal) == expected.Assembly.GetName().Name);

    private static bool CarriesProjection(TypeReference type) => Is(type, Identity) || Is(type, Verified) || Is(type, Feature)
        || type is GenericInstanceType generic && generic.GenericArguments.Any(CarriesProjection)
        || type is TypeSpecification specification && CarriesProjection(specification.ElementType);

    private static bool TouchesProjection(Method method) => CarriesProjection(method.Definition.ReturnType)
        || method.Definition.Parameters.Any(parameter => CarriesProjection(parameter.ParameterType))
        || method.Definition.GenericParameters.SelectMany(parameter => parameter.Constraints).Any(constraint => CarriesProjection(constraint.ConstraintType))
        || method.Owner.GenericParameters.SelectMany(parameter => parameter.Constraints).Any(constraint => CarriesProjection(constraint.ConstraintType))
        || method.Definition.Body.Variables.Any(variable => CarriesProjection(variable.VariableType))
        || method.Definition.Body.Instructions.Any(instruction => instruction.Operand switch
        {
            TypeReference type => CarriesProjection(type),
            FieldReference field => CarriesProjection(field.DeclaringType) || CarriesProjection(field.FieldType),
            MethodReference target => CarriesProjection(target.DeclaringType) || CarriesProjection(target.ReturnType)
                || target.Parameters.Any(parameter => CarriesProjection(parameter.ParameterType))
                || target is GenericInstanceMethod generic && generic.GenericArguments.Any(CarriesProjection),
            _ => false
        });

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
