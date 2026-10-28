using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticationAuthorityTests
{
    private const string OptionalUser = "System.Nullable`1<LearnStack.SharedKernel.Identifiers.UserId>";
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
    private static readonly string[] VerifiedArguments = ["System.String", "System.String", OptionalUser, Tenant, Organization];
    private static readonly string VerifiedConstructor = Signature(Verified, ".ctor", "System.Void", VerifiedArguments);
    private static readonly string VerifiedCopyConstructor = Signature(Verified, ".ctor", "System.Void", Verified);
    private static readonly string IdentityCopyConstructor = Signature(Identity, ".ctor", "System.Void", Identity);
    private static readonly Dictionary<string, string> ApprovedSites = new(StringComparer.Ordinal)
    {
        [Profile + ".TryReadValidated"] = Signature(Profile, "TryReadValidated", "System.Boolean", "System.String", Authentication + "TenantAuthenticationOptions", "System.DateTimeOffset", Verified + "&", Authentication + "TenantTokenFailure&"),
        [Events + ".MessageReceived"] = Signature(Events, "MessageReceived", TaskType, "Microsoft.AspNetCore.Authentication.JwtBearer.MessageReceivedContext"),
        [Events + ".AuthenticationFailed"] = Signature(Events, "AuthenticationFailed", TaskType, "Microsoft.AspNetCore.Authentication.JwtBearer.AuthenticationFailedContext"),
        [Events + ".TokenValidated"] = Signature(Events, "TokenValidated", TaskType, "Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext"),
        [Limiter + ".AttemptAcquire"] = Signature(Limiter, "AttemptAcquire", Authentication + "SubjectAdmissionDecision", Verified),
        [BindingAdmission + ".InvokeAsync"] = Signature(BindingAdmission, "InvokeAsync", TaskType, Http, Authentication + "ITenantIdentityBindingResolver"),
        [BindingAdmission + ".Refuse"] = Signature(BindingAdmission, "Refuse", "System.Void", Http),
        [BindingResolver + ".ResolveExistingAsync"] = Signature(BindingResolver, "ResolveExistingAsync", TaskType + "`1<LearnStack.Modules.Identity.Application.Contracts.Bindings.IdentityBindingResult>", Verified, "System.Threading.CancellationToken"),
        [Provenance + "..ctor"] = Signature(Provenance, ".ctor", "System.Void", Verified),
        [Provenance + ".Matches"] = Signature(Provenance, "Matches", "System.Boolean", Verified),
        [CandidateSource + ".ForValidatedScope"] = Signature(CandidateSource, "ForValidatedScope", "LearnStack.SharedKernel.Permissions.AuthorizationCandidate", "LearnStack.SharedKernel.Identifiers.TenantId", Organization),
        [CandidateSource + ".IsEvidenceCurrent"] = Signature(CandidateSource, "IsEvidenceCurrent", "System.Boolean", "System.Object"),
        [CandidateSource + ".MatchesIdentity"] = Signature(CandidateSource, "MatchesIdentity", "System.Boolean", Identity, Verified),
        [TransportSource + ".Capture"] = Signature(TransportSource, "Capture", "LearnStack.SharedKernel.Audit.OperationTransportProvenance", "LearnStack.SharedKernel.Permissions.OperationInvocation"),
        [TransportSource + ".MatchesScope"] = Signature(TransportSource, "MatchesScope", "System.Boolean", Http, Identity, "LearnStack.SharedKernel.Permissions.OperationInvocation"),
        [Native + "..ctor"] = Signature(Native, ".ctor", "System.Void", Http, NativeSource, Verified, Identity),
        [Native + ".TryMint"] = Signature(Native, "TryMint", Native, Http, Identity),
        [Native + ".IsCurrent"] = Signature(Native, "IsCurrent", "System.Boolean", Http),
        [Native + ".get_Actor"] = Signature(Native, "get_Actor", User),
        [NativeSource + "..ctor"] = Signature(NativeSource, ".ctor", "System.Void", Http, Host, Verified,
            Authentication + "SubjectAdmissionDecision", Provenance, Tenant, Organization, Authentication + "GlobalSelfOperation",
            "LearnStack.Api.Tenancy.EffectiveHostAccessor", Authentication + "NativeSessionSourcePolicy", "LearnStack.SharedKernel.Time.IClock"),
        [NativeSource + ".IsCurrent"] = Signature(NativeSource, "IsCurrent", "System.Boolean", Http),
        [NativeMiddleware + ".InvokeAsync"] = Signature(NativeMiddleware, "InvokeAsync", TaskType, Http,
            "LearnStack.Api.Tenancy.EffectiveHostAccessor", Authentication + "NativeSessionSourcePolicy", "LearnStack.SharedKernel.Time.IClock"),
        [SelfAdmission + ".Allows"] = Signature(SelfAdmission, "Allows", "System.Boolean", "LearnStack.SharedKernel.Identity.GlobalIdentitySubject",
            "LearnStack.Modules.Identity.Application.Contracts.Entry.IdentitySelfOperation", OptionalUser),
        [SelfAdmission + ".CurrentSubject"] = Signature(SelfAdmission, "CurrentSubject", Authentication + "NativeEntrySubject",
            Http, "LearnStack.Modules.Identity.Application.Contracts.Entry.IdentitySelfOperation"),
        [SelfAdmission + ".Capture"] = Signature(SelfAdmission, "Capture", "LearnStack.Modules.Identity.Application.Contracts.Entry.NativeSessionAssociation",
            User, "LearnStack.SharedKernel.Identifiers.TenantId", Organization),
        [Authentication + "OperationClassificationMiddleware.InvokeAsync"] = Signature(Authentication + "OperationClassificationMiddleware", "InvokeAsync", TaskType, Http,
            Authentication + "OperationHttpClassifier", "System.Func`1<LearnStack.Application.Authorization.OperationAuthorizationCoordinator>", Context),
        [Authentication + "OperationAuthorizationHandler.HandleAsync"] = Signature(Authentication + "OperationAuthorizationHandler", "HandleAsync", TaskType,
            "Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext"),
        [CredentialWitness + "..ctor"] = Signature(CredentialWitness, ".ctor", "System.Void", Http, Host, Verified, Identity,
            Authentication + "SubjectAdmissionDecision", Provenance, Native),
        [CredentialWitness + ".get_Subject"] = Signature(CredentialWitness, "get_Subject", Verified),
        [CredentialWitness + ".get_Identity"] = Signature(CredentialWitness, "get_Identity", Identity),
        [Resolver + ".BuildAttemptAsync"] = Signature(Resolver, "BuildAttemptAsync", TaskType + "`1<" + Attempt + ">", Http, Host, ScopeValidator, Membership, Identity),
        [Resolver + ".RecordRefusalAsync"] = Signature(Resolver, "RecordRefusalAsync", TaskType, Attempt, Host, Identity, Recorder),
        [Resolver + ".InvokeAsync"] = Signature(Resolver, "InvokeAsync", TaskType, Http, ScopeValidator, Membership, Recorder),
        ["LearnStack.Api.Tenancy.TenantAssertionMiddleware.InvokeAsync"] = Signature("LearnStack.Api.Tenancy.TenantAssertionMiddleware", "InvokeAsync", TaskType, Http, Context, Recorder,
            "LearnStack.Api.Tenancy.IClaimContextAssertionRecorder"),
        [Authentication + "TenantSurfaceAdmissionMiddleware.InvokeAsync"] = Signature(Authentication + "TenantSurfaceAdmissionMiddleware", "InvokeAsync", TaskType, Http, Context),
        [Authentication + "TenantSurfaceAdmissionMiddleware.HasHostAuthority"] = Signature(Authentication + "TenantSurfaceAdmissionMiddleware", "HasHostAuthority", "System.Boolean", Http, Context, Identity),
        [Authentication + "TenantSubjectAdmissionMiddleware.InvokeAsync"] = Signature(Authentication + "TenantSubjectAdmissionMiddleware", "InvokeAsync", TaskType, Http),
        [Authentication + "TenantCredentialAdmissionMiddleware.InvokeAsync"] = Signature(Authentication + "TenantCredentialAdmissionMiddleware", "InvokeAsync", TaskType, Http),
        [Authentication + "TenantAuthorizationResultHandler.HandleAsync"] = Signature(Authentication + "TenantAuthorizationResultHandler", "HandleAsync", TaskType, "Microsoft.AspNetCore.Http.RequestDelegate", Http, "Microsoft.AspNetCore.Authorization.AuthorizationPolicy", "Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult")
    };

    private static string Signature(string owner, string name, string result, params string[] parameters) =>
        result + " " + owner + "::" + name + "(" + string.Join(",", parameters) + ")";

    private static string ExpectedSite(string site) => site switch
    {
        Feature + ".set_Identity" => Signature(Feature, "set_Identity", "System.Void", Identity),
        Feature + ".set_VerifiedSubject" => Signature(Feature, "set_VerifiedSubject", "System.Void", Verified),
        Feature + "..ctor" => Signature(Feature, ".ctor", "System.Void"),
        Identity + ".<Clone>$" => Signature(Identity, "<Clone>$", Identity),
        Verified + ".<Clone>$" => Signature(Verified, "<Clone>$", Verified),
        _ => ApprovedSites[site]
    };

    private static bool HasClosedIdentityShape(TypeDefinition identity) => HasClosedRecordShape(identity, Identity,
        IdentityArguments, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Issuer"] = "System.String",
            ["Subject"] = "System.String",
            ["ActorUserId"] = User,
            ["TenantId"] = Tenant,
            ["OrganizationId"] = Organization
        });

    private static bool HasClosedVerifiedShape(TypeDefinition identity) => HasClosedRecordShape(identity, Verified,
        VerifiedArguments, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Issuer"] = "System.String",
            ["Subject"] = "System.String",
            ["ClaimedActor"] = OptionalUser,
            ["TenantId"] = Tenant,
            ["OrganizationId"] = Organization
        });

    private static bool HasClosedRecordShape(TypeDefinition identity, string name, string[] arguments,
        Dictionary<string, string> types)
    {
        var expected = new List<string>
        {
            Signature(name, ".ctor", "System.Void", arguments), Signature(name, ".ctor", "System.Void", name),
            Signature(name, "get_EqualityContract", "System.Type"),
            Signature(name, "ToString", "System.String"),
            Signature(name, "PrintMembers", "System.Boolean", "System.Text.StringBuilder"),
            Signature(name, "op_Inequality", "System.Boolean", name, name),
            Signature(name, "op_Equality", "System.Boolean", name, name),
            Signature(name, "GetHashCode", "System.Int32"),
            Signature(name, "Equals", "System.Boolean", "System.Object"),
            Signature(name, "Equals", "System.Boolean", name),
            Signature(name, "<Clone>$", name),
            Signature(name, "Deconstruct", "System.Void", arguments.Select(argument => argument + "&").ToArray())
        };
        foreach (var (property, type) in types)
        {
            expected.Add(Signature(name, "get_" + property, type));
            expected.Add(Signature(name, "set_" + property, Init, type));
        }
        return identity.IsSealed && !identity.IsPublic
            && Same(identity.Methods.Select(method => method.FullName), expected)
            && Same(identity.Properties.Select(property => property.Name), types.Keys.Append("EqualityContract"))
            && identity.Properties.Single(property => property.Name == "EqualityContract") is { SetMethod: null, PropertyType.FullName: "System.Type" }
            && identity.Properties.Where(property => property.Name != "EqualityContract").All(property =>
                property.PropertyType.FullName == types[property.Name]
                && property.GetMethod.ReturnType.FullName == types[property.Name]
                && property.SetMethod.ReturnType.FullName == Init
                && property.SetMethod.Parameters.Single().ParameterType.FullName == types[property.Name])
            && Same(identity.Fields.Select(field => field.Name + ":" + field.FieldType.FullName),
                types.Select(property => "<" + property.Key + ">k__BackingField:" + property.Value))
            && identity.Fields.All(field => field.IsPrivate && field.IsInitOnly && !field.IsStatic)
            && identity.BaseType.FullName == "System.Object"
            && Same(identity.Interfaces.Select(contract => contract.InterfaceType.FullName), ["System.IEquatable`1<" + name + ">"])
            && !identity.HasEvents && !identity.HasNestedTypes && !identity.HasGenericParameters;
    }

    private static bool HasClosedFeatureShape(TypeDefinition feature) =>
        Same(feature.Methods.Select(method => method.FullName),
        [Signature(Feature, ".ctor", "System.Void"), Signature(Feature, "get_Identity", Identity), Signature(Feature, "set_Identity", "System.Void", Identity),
            Signature(Feature, "get_VerifiedSubject", Verified), Signature(Feature, "set_VerifiedSubject", "System.Void", Verified)])
        && Same(feature.Properties.Select(property => property.Name), ["Identity", "VerifiedSubject"])
        && feature.Properties.All(property => Is(property.PropertyType,
            property.Name == "Identity" ? Identity : Verified))
        && Same(feature.Fields.Select(field => field.Name + ":" + field.FieldType.FullName),
            ["<Identity>k__BackingField:" + Identity, "<VerifiedSubject>k__BackingField:" + Verified])
        && feature.Fields.All(field => field.IsPrivate && !field.IsStatic)
        && feature.BaseType.FullName == "System.Object" && !feature.HasInterfaces
        && !feature.HasEvents && !feature.HasNestedTypes && !feature.HasGenericParameters;

    private static bool Same(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool HasClosedProvenanceShape(TypeDefinition provenance) => provenance.IsSealed && !provenance.IsPublic
        && Same(provenance.Methods.Select(method => method.FullName),
            [Signature(Provenance, ".ctor", "System.Void", Verified), Signature(Provenance, "Matches", "System.Boolean", Verified)])
        && provenance.Fields.Count == 1 && provenance.Fields[0] is { Name: "_subject", IsPrivate: true, IsInitOnly: true, IsStatic: false }
        && Is(provenance.Fields[0].FieldType, Verified)
        && !provenance.HasProperties && !provenance.HasEvents && !provenance.HasInterfaces
        && !provenance.HasNestedTypes && !provenance.HasGenericParameters;

    private static bool HasClosedCredentialWitnessShape(TypeDefinition witness)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Http"] = Http,
            ["Host"] = Host,
            ["Subject"] = Verified,
            ["Identity"] = Identity,
            ["Decision"] = Authentication + "SubjectAdmissionDecision",
            ["Provenance"] = Provenance,
            ["Native"] = Native
        };
        return witness.IsSealed && witness.IsNestedPrivate && witness.BaseType.FullName == "System.Object"
            && !witness.HasInterfaces && !witness.HasEvents && !witness.HasNestedTypes && !witness.HasGenericParameters
            && Same(witness.Methods.Select(method => method.FullName), properties.Select(property =>
                    Signature(CredentialWitness, "get_" + property.Key, property.Value))
                .Append(ApprovedSites[CredentialWitness + "..ctor"])
                .Append(Signature(CredentialWitness, "ToString", "System.String")))
            && Same(witness.Properties.Select(property => property.Name), properties.Keys)
            && witness.Properties.All(property => property.SetMethod is null && property.GetMethod.IsAssembly
                && property.PropertyType.FullName == properties[property.Name])
            && Same(witness.Fields.Select(field => field.Name + ":" + field.FieldType.FullName), properties.Select(property =>
                "<" + property.Key + ">k__BackingField:" + property.Value))
            && witness.Fields.All(field => field.IsPrivate && field.IsInitOnly && !field.IsStatic)
            && witness.Methods.Single(method => method.Name == "ToString").Body.Instructions
                .Where(instruction => instruction.OpCode != OpCodes.Nop).Select(instruction => instruction.OpCode.Code)
                .SequenceEqual([Code.Ldstr, Code.Ret])
            && witness.Methods.Single(method => method.Name == "ToString").Body.Instructions
                .Single(instruction => instruction.OpCode == OpCodes.Ldstr).Operand as string == "CredentialWitness";
    }

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
            or "System.Void System.ObjectDisposedException::ThrowIf(System.Boolean,System.Object)"
            or "System.Boolean System.Object::ReferenceEquals(System.Object,System.Object)",
        "System.Threading" => target.FullName is
            "System.Void System.Threading.Monitor::Enter(System.Object,System.Boolean&)"
            or "System.Void System.Threading.Monitor::Exit(System.Object)",
        _ => false
    };

    private static bool ExportsProjection(Method method) => ProjectionExports(method).Any();

    private static IEnumerable<Instruction> ProjectionExports(Method method) => method.Definition.Body.Instructions.Where(instruction =>
        // These owners keep typed projection state; erasing it into object
        // storage, a boxed value or a reference array creates another channel.
        instruction.OpCode.Code is Code.Box or Code.Stelem_Ref
        || instruction.OpCode.Code is Code.Stfld or Code.Stsfld && instruction.Operand is FieldReference field
            && IsOpaqueProjectionArgument(field.FieldType, owner: field.DeclaringType)
        || instruction.Operand is MethodReference target && ExportsProjection(target)
            && !IsClosedWitnessForwarding(method, instruction, target)
            && !IsClosedUserAgentFormatting(method, instruction, target)
            && !IsClosedHttpEndpointForwarding(method, instruction, target));

    private static bool IsClosedRolesAbsenceCheck(Call call)
    {
        if (call.Source.Site != Signature(Authentication + "OperationHttpClassifier", "Classify", Authentication + "HttpOperationClassification",
                "Microsoft.AspNetCore.Http.Endpoint", "LearnStack.SharedKernel.Permissions.OperationPolicy&")
            || call.Target.FullName != "System.String Microsoft.AspNetCore.Authorization.IAuthorizeData::get_Roles()")
            return false;
        var reads = call.Source.Definition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
            && target.FullName == call.Target.FullName).ToArray();
        return reads.Length == 1 && reads[0].OpCode == OpCodes.Callvirt
            && reads[0].Next is { OpCode.Code: Code.Call, Operand: MethodReference empty }
            && empty.FullName == "System.Boolean System.String::IsNullOrEmpty(System.String)"
            && reads[0].Next.Next is { OpCode.Code: Code.Brtrue or Code.Brtrue_S, Operand: Instruction admitted } branch
            && branch.Next?.OpCode == OpCodes.Ldc_I4_1 && branch.Next.Next?.OpCode == OpCodes.Ret
            && admitted.OpCode == OpCodes.Ldarg_2 && admitted.Next?.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3
            && admitted.Next.Next?.OpCode == OpCodes.Stind_Ref && admitted.Next.Next.Next?.OpCode == OpCodes.Ldc_I4_2
            && admitted.Next.Next.Next.Next?.OpCode == OpCodes.Ret;
    }

    // CheckHttp's opaque argument is exactly the selected Endpoint from the same
    // HttpContext as its cancellation token, never a projection or caller object.
    private static bool IsClosedHttpEndpointForwarding(Method method, Instruction instruction, MethodReference target)
    {
        if (method.Site != ApprovedSites[Authentication + "OperationAuthorizationHandler.HandleAsync"]
            && method.Site != ApprovedSites[Authentication + "OperationClassificationMiddleware.InvokeAsync"]
            || instruction.OpCode != OpCodes.Callvirt
            || target.FullName != "System.Threading.Tasks.Task`1<LearnStack.SharedKernel.Results.Result`1<LearnStack.SharedKernel.Results.None>> LearnStack.Application.Authorization.OperationAuthorizationCoordinator::CheckHttpAsync(System.Type,System.Object,System.Threading.CancellationToken)"
            || instruction.Previous?.Operand is not MethodReference cancellation
            || cancellation.FullName != "System.Threading.CancellationToken Microsoft.AspNetCore.Http.HttpContext::get_RequestAborted()"
            || instruction.Previous.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference http }
            || http.FieldType.FullName != Http || instruction.Previous.Previous.Previous?.OpCode != OpCodes.Ldarg_0
            || instruction.Previous.Previous.Previous.Previous is not { } endpoint
            || endpoint.Operand is not MethodReference acquisition
            || acquisition.FullName != "Microsoft.AspNetCore.Http.Endpoint Microsoft.AspNetCore.Http.EndpointHttpContextExtensions::GetEndpoint(Microsoft.AspNetCore.Http.HttpContext)"
            || endpoint.Previous is not { OpCode.Code: Code.Ldfld, Operand: FieldReference endpointHttp }
            || endpointHttp.FullName != http.FullName || endpoint.Previous.Previous?.OpCode != OpCodes.Ldarg_0
            || endpoint.Previous.Previous.Previous?.Operand is not MethodReference policy)
            return false;
        return policy.FullName == "System.Type LearnStack.SharedKernel.Permissions.OperationPolicy::get_RequestType()";
    }

    // Release emits a constrained Object.ToString for the StringValues value.
    // Permit only the actual User-Agent local, passed directly as ForTransport's
    // final string argument; identity/object formatting elsewhere stays opaque.
    private static bool IsClosedUserAgentFormatting(Method method, Instruction instruction, MethodReference target)
    {
        const string StringValues = "Microsoft.Extensions.Primitives.StringValues";
        if (method.Site != ApprovedSites[TransportSource + ".Capture"]
            || target.FullName != "System.String System.Object::ToString()"
            || instruction.Previous is not { OpCode.Code: Code.Constrained, Operand: TypeReference constrained }
            || constrained.FullName != StringValues
            || instruction.Previous.Previous is not { OpCode.Code: Code.Ldloca or Code.Ldloca_S, Operand: VariableDefinition local } receiver
            || local.VariableType.FullName != StringValues)
            return false;
        var stores = method.Definition.Body.Instructions.Where(candidate => StoresLocal(candidate, local)).ToArray();
        if (stores.Length != 1
            || stores[0].Previous?.Operand is not MethodReference header
            || header.FullName != "Microsoft.Extensions.Primitives.StringValues Microsoft.AspNetCore.Http.IHeaderDictionary::get_UserAgent()"
            || stores[0].Previous.Previous?.Operand is not MethodReference headers
            || headers.FullName != "Microsoft.AspNetCore.Http.IHeaderDictionary Microsoft.AspNetCore.Http.HttpRequest::get_Headers()"
            || stores[0].Previous.Previous.Previous?.Operand is not MethodReference request
            || request.FullName != "Microsoft.AspNetCore.Http.HttpRequest Microsoft.AspNetCore.Http.HttpContext::get_Request()")
            return false;
        return receiver.Previous is { OpCode.Code: Code.Brfalse or Code.Brfalse_S, Operand: Instruction absent }
            && receiver.Previous.Previous?.Operand is MethodReference count
            && count.FullName == "System.Int32 Microsoft.Extensions.Primitives.StringValues::get_Count()"
            && receiver.Previous.Previous.Previous is { OpCode.Code: Code.Ldloca or Code.Ldloca_S, Operand: VariableDefinition counted }
            && counted == local
            && instruction.Next is { OpCode.Code: Code.Br or Code.Br_S, Operand: Instruction transport } jump
            && jump.Next == absent && absent.OpCode == OpCodes.Ldnull && absent.Next == transport
            && transport.Operand is MethodReference mint
            && mint.FullName == "LearnStack.SharedKernel.Audit.OperationTransportProvenance LearnStack.SharedKernel.Audit.OperationTransportProvenance/MintingSource::ForTransport(LearnStack.SharedKernel.Permissions.OperationInvocation,System.String,System.Net.IPAddress,System.String)"
            && transport.Next?.OpCode == OpCodes.Ret;
    }

    private static bool StoresLocal(Instruction instruction, VariableDefinition local) => instruction.OpCode.Code switch
    {
        Code.Stloc or Code.Stloc_S => instruction.Operand == local,
        Code.Stloc_0 => local.Index == 0,
        Code.Stloc_1 => local.Index == 1,
        Code.Stloc_2 => local.Index == 2,
        Code.Stloc_3 => local.Index == 3,
        _ => false
    };

    private static bool IsClosedWitnessForwarding(Method method, Instruction instruction, MethodReference target) =>
        method.Site == ApprovedSites[CandidateSource + ".ForValidatedScope"]
        && target.FullName == "LearnStack.SharedKernel.Permissions.AuthorizationCandidate LearnStack.SharedKernel.Permissions.AuthorizationCandidate/MintingSource::ForCandidate(LearnStack.SharedKernel.Permissions.AuthorizationSourceKind,LearnStack.SharedKernel.Identifiers.UserId,LearnStack.SharedKernel.Identifiers.TenantId,System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>,System.Object)"
        && instruction.Previous is { OpCode.Code: Code.Newobj, Operand: MethodReference constructor }
        && constructor.FullName == ApprovedSites[CredentialWitness + "..ctor"];

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
