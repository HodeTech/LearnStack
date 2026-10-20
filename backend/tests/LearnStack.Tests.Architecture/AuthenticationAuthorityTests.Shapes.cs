using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticationAuthorityTests
{
    private const string User = "LearnStack.SharedKernel.Identifiers.UserId";
    private const string Tenant = "System.Nullable`1<LearnStack.SharedKernel.Identifiers.TenantId>";
    private const string Organization = "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>";
    private const string TaskType = "System.Threading.Tasks.Task";
    private const string Http = "Microsoft.AspNetCore.Http.HttpContext";
    private const string Context = "LearnStack.SharedKernel.Tenancy.ITenantContext";
    private const string Recorder = "LearnStack.Api.Tenancy.ITenantAssertionRecorder";
    private const string ScopeValidator = "LearnStack.SharedKernel.Tenancy.IOrganizationScopeValidator";
    private const string Membership = "LearnStack.SharedKernel.Tenancy.ITenantMembershipReader";
    private const string Attempt = "LearnStack.SharedKernel.Tenancy.TenantResolutionAttempt";
    private const string Host = "LearnStack.Api.Tenancy.HostClassification";
    private const string Init = "System.Void modreq(System.Runtime.CompilerServices.IsExternalInit)";
    private static readonly string[] IdentityArguments = ["System.String", "System.String", User, Tenant, Organization];
    private static readonly string IdentityConstructor = Signature(Identity, ".ctor", "System.Void", IdentityArguments);
    private static readonly string IdentityCopyConstructor = Signature(Identity, ".ctor", "System.Void", Identity);
    private static readonly Dictionary<string, string> ApprovedSites = new(StringComparer.Ordinal)
    {
        [Profile + ".TryReadValidated"] = Signature(Profile, "TryReadValidated", "System.Boolean", "System.String", Authentication + "TenantAuthenticationOptions", "System.DateTimeOffset", Identity + "&", Authentication + "TenantTokenFailure&"),
        [Events + ".MessageReceived"] = Signature(Events, "MessageReceived", TaskType, "Microsoft.AspNetCore.Authentication.JwtBearer.MessageReceivedContext"),
        [Events + ".AuthenticationFailed"] = Signature(Events, "AuthenticationFailed", TaskType, "Microsoft.AspNetCore.Authentication.JwtBearer.AuthenticationFailedContext"),
        [Events + ".TokenValidated"] = Signature(Events, "TokenValidated", TaskType, "Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext"),
        [Limiter + ".AttemptAcquire"] = Signature(Limiter, "AttemptAcquire", Authentication + "SubjectAdmissionDecision", Identity),
        [Resolver + ".BuildAttemptAsync"] = Signature(Resolver, "BuildAttemptAsync", TaskType + "`1<" + Attempt + ">", Http, Host, ScopeValidator, Membership, Identity),
        [Resolver + ".RecordRefusalAsync"] = Signature(Resolver, "RecordRefusalAsync", TaskType, Attempt, Host, Identity, Recorder),
        [Resolver + ".InvokeAsync"] = Signature(Resolver, "InvokeAsync", TaskType, Http, ScopeValidator, Membership, Recorder),
        ["LearnStack.Api.Tenancy.TenantAssertionMiddleware.InvokeAsync"] = Signature("LearnStack.Api.Tenancy.TenantAssertionMiddleware", "InvokeAsync", TaskType, Http, Context, Recorder),
        [Authentication + "TenantSurfaceAdmissionMiddleware.InvokeAsync"] = Signature(Authentication + "TenantSurfaceAdmissionMiddleware", "InvokeAsync", TaskType, Http, Context),
        [Authentication + "TenantSurfaceAdmissionMiddleware.HasHostAuthority"] = Signature(Authentication + "TenantSurfaceAdmissionMiddleware", "HasHostAuthority", "System.Boolean", Context, Identity),
        [Authentication + "TenantSubjectAdmissionMiddleware.InvokeAsync"] = Signature(Authentication + "TenantSubjectAdmissionMiddleware", "InvokeAsync", TaskType, Http),
        [Authentication + "TenantCredentialAdmissionMiddleware.InvokeAsync"] = Signature(Authentication + "TenantCredentialAdmissionMiddleware", "InvokeAsync", TaskType, Http),
        [Authentication + "TenantAuthorizationResultHandler.HandleAsync"] = Signature(Authentication + "TenantAuthorizationResultHandler", "HandleAsync", TaskType, "Microsoft.AspNetCore.Http.RequestDelegate", Http, "Microsoft.AspNetCore.Authorization.AuthorizationPolicy", "Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult")
    };

    private static string Signature(string owner, string name, string result, params string[] parameters) =>
        result + " " + owner + "::" + name + "(" + string.Join(",", parameters) + ")";

    private static string ExpectedSite(string site) => site switch
    {
        Feature + ".set_Identity" => Signature(Feature, "set_Identity", "System.Void", Identity),
        Feature + "..ctor" => Signature(Feature, ".ctor", "System.Void"),
        Identity + ".<Clone>$" => Signature(Identity, "<Clone>$", Identity),
        _ => ApprovedSites[site]
    };

    private static bool HasClosedIdentityShape(TypeDefinition identity)
    {
        var expected = new List<string>
        {
            IdentityConstructor, IdentityCopyConstructor,
            Signature(Identity, "get_EqualityContract", "System.Type"),
            Signature(Identity, "ToString", "System.String"),
            Signature(Identity, "PrintMembers", "System.Boolean", "System.Text.StringBuilder"),
            Signature(Identity, "op_Inequality", "System.Boolean", Identity, Identity),
            Signature(Identity, "op_Equality", "System.Boolean", Identity, Identity),
            Signature(Identity, "GetHashCode", "System.Int32"),
            Signature(Identity, "Equals", "System.Boolean", "System.Object"),
            Signature(Identity, "Equals", "System.Boolean", Identity),
            Signature(Identity, "<Clone>$", Identity),
            Signature(Identity, "Deconstruct", "System.Void", IdentityArguments.Select(argument => argument + "&").ToArray())
        };
        var types = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Issuer"] = "System.String",
            ["Subject"] = "System.String",
            ["ActorUserId"] = User,
            ["TenantId"] = Tenant,
            ["OrganizationId"] = Organization
        };
        foreach (var (property, type) in types)
        {
            expected.Add(Signature(Identity, "get_" + property, type));
            expected.Add(Signature(Identity, "set_" + property, Init, type));
        }
        return Same(identity.Methods.Select(method => method.FullName), expected)
            && Same(identity.Properties.Select(property => property.Name), IdentityProperties.Append("EqualityContract"))
            && identity.Properties.Single(property => property.Name == "EqualityContract") is { SetMethod: null, PropertyType.FullName: "System.Type" }
            && Same(identity.Fields.Select(field => field.Name + ":" + field.FieldType.FullName),
                types.Select(property => "<" + property.Key + ">k__BackingField:" + property.Value))
            && identity.Fields.All(field => field.IsPrivate && field.IsInitOnly && !field.IsStatic)
            && identity.BaseType.FullName == "System.Object"
            && Same(identity.Interfaces.Select(contract => contract.InterfaceType.FullName), ["System.IEquatable`1<" + Identity + ">"])
            && !identity.HasEvents && !identity.HasNestedTypes && !identity.HasGenericParameters;
    }

    private static bool HasClosedFeatureShape(TypeDefinition feature) =>
        Same(feature.Methods.Select(method => method.FullName),
        [Signature(Feature, ".ctor", "System.Void"), Signature(Feature, "get_Identity", Identity), Signature(Feature, "set_Identity", "System.Void", Identity)])
        && feature.Properties.Count == 1 && feature.Properties[0].Name == "Identity"
        && feature.Fields.Count == 1 && feature.Fields[0] is { Name: "<Identity>k__BackingField", IsPrivate: true, IsStatic: false }
        && Is(feature.Fields[0].FieldType, Identity)
        && feature.BaseType.FullName == "System.Object" && !feature.HasInterfaces
        && !feature.HasEvents && !feature.HasNestedTypes && !feature.HasGenericParameters;

    private static bool Same(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool IsOpaqueProjectionArgument(TypeReference type, MethodReference? method = null, TypeReference? owner = null)
    {
        if (type is GenericParameter parameter)
        {
            if (parameter.Type == GenericParameterType.Method && method is GenericInstanceMethod genericMethod)
                return IsOpaqueProjectionArgument(genericMethod.GenericArguments[parameter.Position]);
            if (parameter.Type == GenericParameterType.Type && (owner ?? method?.DeclaringType) is GenericInstanceType genericType)
                return IsOpaqueProjectionArgument(genericType.GenericArguments[parameter.Position]);
        }
        return type.FullName == "System.Object"
            || type is ArrayType or ByReferenceType && IsOpaqueProjectionArgument(((TypeSpecification)type).ElementType, method, owner);
    }

    // These exact framework operations neither retain nor format the argument's
    // contents. All other opaque forwarding from a projection owner is refused.
    private static bool IsNonExportingFrameworkCall(MethodReference target) => target.DeclaringType.Scope.Name switch
    {
        "System.Runtime" => target.FullName is
            "System.Void System.ArgumentNullException::ThrowIfNull(System.Object,System.String)"
            or "System.Void System.ObjectDisposedException::ThrowIf(System.Boolean,System.Object)",
        "System.Threading" => target.FullName is
            "System.Void System.Threading.Monitor::Enter(System.Object,System.Boolean&)"
            or "System.Void System.Threading.Monitor::Exit(System.Object)",
        _ => false
    };

    private static bool ExportsProjection(Method method) => method.Definition.Body.Instructions.Any(instruction =>
        // These owners keep typed projection state; erasing it into object
        // storage, a boxed value or a reference array creates another channel.
        instruction.OpCode.Code is Code.Box or Code.Stelem_Ref
        || instruction.OpCode.Code is Code.Stfld or Code.Stsfld && instruction.Operand is FieldReference field
            && IsOpaqueProjectionArgument(field.FieldType, owner: field.DeclaringType)
        || instruction.Operand is MethodReference target && ExportsProjection(target));

    private static bool ExportsProjection(MethodReference target) =>
        // At these few trust-boundary owners an untyped object/object[]
        // argument is itself an export capability. Do not infer safety from
        // the helper's body or name: neither needs to mention the projection.
        target.Parameters.Any(parameter => IsOpaqueProjectionArgument(parameter.ParameterType, target))
            && !IsNonExportingFrameworkCall(target)
        || target.DeclaringType.FullName == "System.Object" && target.Name == "ToString"
        || target.DeclaringType.FullName == "System.String" && target.Name is "Format" or "Concat"
            && target.Parameters.Any(parameter => parameter.ParameterType.FullName is "System.Object" or "System.Object[]")
        || target is GenericInstanceMethod generic
            && generic.GenericArguments.Any(argument => CarriesProjection(argument)
                || argument.FullName == "System.Object" && !(target.DeclaringType.FullName == "System.Linq.Enumerable" && target.Name == "ToDictionary"))
            && !(target.DeclaringType.FullName == "Microsoft.AspNetCore.Http.Features.IFeatureCollection" && target.Name is "Get" or "Set");
}
