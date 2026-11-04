using System.Security.Claims;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticationAuthorityTests
{
    /// <summary>ADR-0064: compiled dirty/clean claim readers exercise the complete call-site predicate.</summary>
    [Fact]
    public void Authority_census_rejects_compiled_claim_role_and_projection_bypasses() => WithProduction(modules =>
    {
        Offenders(modules).Should().BeEmpty("the real production graph is the clean control");
        using var probes = ModuleDefinition.ReadModule(typeof(AuthenticationAuthorityTests).Assembly.Location);
        var type = probes.GetType(typeof(AuthenticationAuthorityTests).FullName).NestedTypes.Single(candidate => candidate.Name == nameof(AuthorityProbe));
        var methods = Methods([type]).ToArray();
        foreach (var name in new[] { nameof(AuthorityProbe.Role), nameof(AuthorityProbe.InterfaceRole), nameof(AuthorityProbe.IdentityName),
                     nameof(AuthorityProbe.Claim), nameof(AuthorityProbe.ClaimSet),
                     nameof(AuthorityProbe.Email), nameof(AuthorityProbe.Authenticated), nameof(AuthorityProbe.RawUser),
                     nameof(AuthorityProbe.AuthenticatedPrincipal), nameof(AuthorityProbe.TicketPrincipal),
                     nameof(AuthorityProbe.AuthorizePolicy), nameof(AuthorityProbe.AuthorizeRequirements),
                     nameof(AuthorityProbe.RolePolicy), nameof(AuthorityProbe.ClaimPolicy), nameof(AuthorityProbe.InterfaceRoleMetadata),
                     nameof(AuthorityProbe.RawTokenClaim), nameof(AuthorityProbe.RawTokenSubject),
                     nameof(AuthorityProbe.LegacyTokenPayload), nameof(AuthorityProbe.RawHeaderReparse),
                     nameof(AuthorityProbe.ValidationClaims), nameof(AuthorityProbe.ValidatedSecurityToken) })
        {
            var dirty = methods.Single(method => method.Name == name);
            Offenders(modules, [dirty]).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal), name);
            Action blinded = () => Offenders(modules, [dirty], _ => false).Should()
                .Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal));
            blinded.Should().Throw<Xunit.Sdk.XunitException>("a weakened raw-reader predicate must fail its dirty control: " + name);
        }
        foreach (var name in new[] { nameof(AuthorityProbe.Forge), nameof(AuthorityProbe.ForgeFeature),
                     nameof(AuthorityProbe.ForgeVerified), nameof(AuthorityProbe.CloneVerified), nameof(AuthorityProbe.VerifiedText),
                     nameof(AuthorityProbe.Clone), nameof(AuthorityProbe.SubjectPermission), nameof(AuthorityProbe.SubjectDeconstruct),
                     nameof(AuthorityProbe.SubjectText), nameof(AuthorityProbe.SubjectInterpolation), nameof(AuthorityProbe.FeatureFormatting) })
            Offenders(modules, [methods.Single(method => method.Name == name)]).Should().NotBeEmpty(name);
        foreach (var name in new[] { nameof(AuthorityProbe.SubjectText), nameof(AuthorityProbe.SubjectInterpolation), nameof(AuthorityProbe.FeatureFormatting) })
        {
            var dirty = methods.Single(method => method.Name == name);
            Offenders(modules, [dirty]).Should().Contain(value => value.StartsWith("Unapproved typed identity/feature consumer:", StringComparison.Ordinal), name);
            Action blinded = () => Offenders(modules, [dirty], projectionUse: _ => false).Should()
                .Contain(value => value.StartsWith("Unapproved typed identity/feature consumer:", StringComparison.Ordinal));
            blinded.Should().Throw<Xunit.Sdk.XunitException>("an object/generic export must exercise the acquisition/forwarding predicate: " + name);
        }
        Offenders(modules, [methods.Single(method => method.Name == nameof(AuthorityProbe.TypedActor))])
            .Should().BeEmpty("an application UserId is not raw provider-claim authority");
        Offenders(modules).Should().BeEmpty("compiled probes were additions to the scan, not replacement production subjects");
    });

    /// <summary>ADR-0064: discovery, immutable shape and same-owner extra writes cannot pass vacuously.</summary>
    [Fact]
    public void Authority_census_detects_missing_subjects_mutability_and_extra_trust_writes() => WithProduction(modules =>
    {
        Offenders(modules).Should().BeEmpty();
        Offenders(modules.Where(module => module.Assembly.Name.Name != "LearnStack.Api").ToArray()).Should().NotBeEmpty();
        var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
        var identity = api.GetType(Identity);
        var feature = api.GetType(Feature);
        var events = api.GetType(Events);
        var profile = api.GetType(Profile);
        var factory = profile.Methods.Single(method => method.Name == "TryReadValidated");
        var construction = factory.Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Newobj
            && instruction.Operand is MethodReference target && Is(target.DeclaringType, Verified));
        var original = construction.Operand;
        construction.Operand = api.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
        Offenders(modules).Should().NotBeEmpty("the actual factory call is a required nonempty subject");
        construction.Operand = original;

        identity.IsSealed = false;
        Offenders(modules).Should().NotBeEmpty("the projection cannot gain a mutable/subclass authority channel");
        identity.IsSealed = true;
        var verified = api.GetType(Verified);
        verified.IsSealed = false;
        Offenders(modules).Should().NotBeEmpty("the verified subject cannot gain a subclass authority channel");
        verified.IsSealed = true;
        var verifiedField = verified.Fields.First();
        verifiedField.IsInitOnly = false;
        Offenders(modules).Should().NotBeEmpty("verified evidence must remain immutable for cached quota provenance");
        verifiedField.IsInitOnly = true;
        var witness = api.GetType(CandidateSource).NestedTypes.Single(type => type.FullName == CredentialWitness);
        var witnessField = witness.Fields.Single(candidate => Is(candidate.FieldType, Identity));
        witnessField.IsInitOnly = false;
        Offenders(modules).Should().Contain("Candidate credential witness must retain its private immutable nonformatting shape.");
        witnessField.IsInitOnly = true;
        var witnessText = witness.Methods.Single(method => method.Name == "ToString").Body.Instructions
            .Single(instruction => instruction.OpCode == OpCodes.Ldstr);
        var fixedText = witnessText.Operand;
        witnessText.Operand = "Unexpected credential text";
        Offenders(modules).Should().Contain("Candidate credential witness must retain its private immutable nonformatting shape.");
        witnessText.Operand = fixedText;
        var transport = api.GetType(TransportSource).Methods.Single(method => method.Name == "Capture");
        var formatting = transport.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.FullName == "System.String System.Object::ToString()");
        var constrained = formatting.Previous;
        var headerType = constrained.Operand;
        constrained.Operand = identity;
        Offenders(modules).Should().Contain("Opaque typed identity/feature export: " + transport.FullName,
            "only the constrained User-Agent value can use the transport formatting exception");
        constrained.Operand = headerType;
        var headerRead = transport.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.FullName == "Microsoft.Extensions.Primitives.StringValues Microsoft.AspNetCore.Http.IHeaderDictionary::get_UserAgent()");
        var headerGetter = headerRead.Operand;
        headerRead.OpCode = OpCodes.Nop;
        headerRead.Operand = null;
        Offenders(modules).Should().Contain("Opaque typed identity/feature export: " + transport.FullName,
            "the formatter must retain the actual User-Agent acquisition, not just a StringValues local");
        headerRead.OpCode = OpCodes.Callvirt;
        headerRead.Operand = headerGetter;
        Offenders(modules).Should().BeEmpty("restoring actual header acquisition and formatting restores the clean graph");
        var classifier = api.GetType(Authentication + "OperationHttpClassifier").Methods.Single(method => method.Name == "Classify");
        var rolesRead = classifier.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.FullName == "System.String Microsoft.AspNetCore.Authorization.IAuthorizeData::get_Roles()");
        var emptyCheck = rolesRead.Next;
        var predicate = emptyCheck.Operand;
        emptyCheck.OpCode = OpCodes.Nop;
        emptyCheck.Operand = null;
        Offenders(modules).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal),
            "a role read is allowed only to refuse nonempty metadata immediately");
        emptyCheck.OpCode = OpCodes.Call;
        emptyCheck.Operand = predicate;
        var emptyBranch = emptyCheck.Next;
        var admittedWhenEmpty = emptyBranch.OpCode;
        emptyBranch.OpCode = OpCodes.Brfalse_S;
        Offenders(modules).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal),
            "nonempty role metadata cannot enter the admitted branch");
        emptyBranch.OpCode = admittedWhenEmpty;
        var metadataCheck = classifier.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.Name == "HasCanonicalKeyMetadata");
        var metadataPredicate = metadataCheck.Operand;
        metadataCheck.Operand = classifier.DeclaringType.Methods.Single(method => method.Name == "IsFrameworkMethodRejection");
        Offenders(modules).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal),
            "an unrelated boolean predicate cannot replace canonical key-metadata admission");
        metadataCheck.Operand = metadataPredicate;
        var metadataBranch = metadataCheck.Next;
        var admittedWhenCanonical = metadataBranch.OpCode;
        metadataBranch.OpCode = OpCodes.Brfalse_S;
        Offenders(modules).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal),
            "noncanonical key metadata cannot enter the admitted branch");
        metadataBranch.OpCode = OpCodes.Br_S;
        Offenders(modules).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal),
            "the key-metadata result must be consumed, not bypassed with an unconditional branch");
        metadataBranch.OpCode = admittedWhenCanonical;
        metadataBranch.Next.OpCode = OpCodes.Ldc_I4_2;
        Offenders(modules).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal),
            "the failed key-metadata gate must return Refused, never Application");
        metadataBranch.Next.OpCode = OpCodes.Ldc_I4_1;
        var metadataEntry = emptyBranch.Operand;
        emptyBranch.Operand = metadataBranch.Operand;
        Offenders(modules).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal),
            "empty roles cannot skip the key-metadata gate and publish a policy directly");
        emptyBranch.Operand = metadataEntry;
        var publishedPolicy = ((Instruction)metadataBranch.Operand).Next;
        var policyLoad = publishedPolicy.OpCode;
        var policyLocal = publishedPolicy.Operand;
        publishedPolicy.OpCode = OpCodes.Ldnull;
        publishedPolicy.Operand = null;
        Offenders(modules).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal),
            "Application admission must publish the same policy whose descriptor passed the metadata gate");
        publishedPolicy.OpCode = policyLoad;
        publishedPolicy.Operand = policyLocal;
        Offenders(modules).Should().BeEmpty("restoring the role absence, key-metadata gate and exact policy publication restores the clean graph");
        foreach (var type in new[] { Authentication + "OperationAuthorizationHandler", Authentication + "OperationClassificationMiddleware" })
        {
            var operation = Methods([api.GetType(type)]).Single(method => method.Definition.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference target && Is(target.DeclaringType, Feature) && target.Name == "get_Identity"));
            if (type == Authentication + "OperationClassificationMiddleware")
            {
                // The handler delegates to the typed replay admission owner; only
                // classification still forwards an opaque endpoint directly here.
                var check = operation.Definition.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target && target.Name == "CheckHttpAsync");
                var endpoint = check.Previous.Previous.Previous.Previous;
                var acquisition = endpoint.Operand;
                endpoint.OpCode = OpCodes.Ldnull;
                endpoint.Operand = null;
                Offenders(modules).Should().Contain("Opaque typed identity/feature export: " + operation.Site,
                    "the opaque operation argument must be the actual same-request endpoint");
                endpoint.OpCode = OpCodes.Call;
                endpoint.Operand = acquisition;
            }
            var identityRead = operation.Definition.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && Is(target.DeclaringType, Feature) && target.Name == "get_Identity");
            var format = new[] { Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Callvirt,
                api.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!)), Instruction.Create(OpCodes.Pop) };
            var processor = operation.Definition.Body.GetILProcessor();
            var previous = identityRead;
            foreach (var instruction in format) { processor.InsertAfter(previous, instruction); previous = instruction; }
            Offenders(modules).Should().Contain("Opaque typed identity/feature export: " + operation.Site,
                "both HTTP authorization adapters must reject extra formatting of their actual identity");
            foreach (var instruction in format) processor.Remove(instruction);
            Offenders(modules).Should().BeEmpty("restoring endpoint acquisition and removing identity formatting restores the exact owner");
        }
        var actor = identity.Properties.Single(property => property.Name == "ActorUserId").SetMethod;
        var init = actor.ReturnType;
        actor.ReturnType = api.TypeSystem.Void;
        Offenders(modules).Should().NotBeEmpty("ordinary mutable setters violate the projection shape");
        actor.ReturnType = init;

        var property = identity.Properties.Single(candidate => candidate.Name == "ActorUserId");
        var actorType = property.PropertyType;
        property.PropertyType = api.TypeSystem.String;
        Offenders(modules).Should().NotBeEmpty("a named but untyped actor is not the approved projection");
        property.PropertyType = actorType;

        var validated = events.Methods.Single(method => method.Name == "TokenValidated");
        var setter = validated.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && Is(target.DeclaringType, Feature) && target.Name == "set_VerifiedSubject");
        var duplicate = Instruction.Create(OpCodes.Callvirt, (MethodReference)setter.Operand);
        validated.Body.Instructions.Add(duplicate);
        Offenders(modules).Should().NotBeEmpty("an extra write inside the approved owner is still a changed capability multiset");
        validated.Body.Instructions.Remove(duplicate);

        var field = feature.Fields.Single(candidate => Is(candidate.FieldType, Identity));
        var bypass = new MethodDefinition("BypassIdentitySetter", MethodAttributes.Private, api.TypeSystem.Void);
        bypass.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, field));
        feature.Methods.Add(bypass);
        Offenders(modules).Should().Contain(value => value.StartsWith("Direct identity backing-field write:", StringComparison.Ordinal));
        feature.Methods.Remove(bypass);
        var action = api.GetType("LearnStack.Api.Account.AccountController").Methods.Single(method => method.Name == "GetSite");
        // Exact framework metadata must exercise the Roles named-property
        // branch independently of the derived-attribute type classifier.
        var nonController = api.GetType(Profile).Methods.Single(method => method.Name == "TryReadValidated");
        var baseAuthorization = new CustomAttribute(api.ImportReference(typeof(AuthorizeAttribute).GetConstructor(Type.EmptyTypes)!));
        nonController.CustomAttributes.Add(baseAuthorization);
        try
        {
            Offenders(modules).Should().BeEmpty("a plain base AuthorizeAttribute does not itself grant realm-role authority");
            baseAuthorization.Properties.Add(new CustomAttributeNamedArgument(nameof(AuthorizeAttribute.Roles),
                new CustomAttributeArgument(api.TypeSystem.String, "tenant-admin")));
            Offenders(modules).Should().Contain(value => value == "Realm-role authorization metadata: " + nonController.DeclaringType.FullName,
                "exact base AuthorizeAttribute.Roles on a non-controller method must be detected by the named-property branch");
            baseAuthorization.Properties.Clear();
            Offenders(modules).Should().BeEmpty("removing Roles restores the clean base attribute control");
        }
        finally { nonController.CustomAttributes.Remove(baseAuthorization); }

        AuthenticationMetadata.WithCompiledRole(api, metadata =>
        {
            action.CustomAttributes.Add(metadata);
            try
            {
                Offenders(modules).Should().Contain(value => value.StartsWith("Realm-role authorization metadata:", StringComparison.Ordinal),
                    "the framework reads Roles on derived authorization attributes too");
                Action blinded = () => Offenders(modules, authorizationData: type => type.FullName == typeof(AuthorizeAttribute).FullName)
                    .Should().Contain(value => value.StartsWith("Realm-role authorization metadata:", StringComparison.Ordinal));
                blinded.Should().Throw<Xunit.Sdk.XunitException>("concrete-only metadata matching must fail its compiled control");
            }
            finally { action.CustomAttributes.Remove(metadata); }
        });
        AuthenticationMetadata.WithExternalRole(api, metadata =>
        {
            action.CustomAttributes.Add(metadata);
            try
            {
                Offenders(modules).Should().Contain(value => value.StartsWith("Realm-role authorization metadata:", StringComparison.Ordinal));
                metadata.Properties.Clear();
                Offenders(modules).Should().Contain(value => value.StartsWith("Realm-role authorization metadata:", StringComparison.Ordinal),
                    "an external constructor can supply Roles without a named attribute argument");
            }
            finally { action.CustomAttributes.Remove(metadata); }
        });
        var unresolved = new TypeReference("Missing.Package", "UnknownAuthorizationAttribute", api,
            new AssemblyNameReference("Missing.Authorization.Package", new Version(1, 0)));
        AuthenticationMetadata.IsData(unresolved, new Dictionary<string, TypeDefinition>()).Should().BeTrue(
            "missing external metadata cannot establish that an attribute is harmless");
        var unresolvedAttribute = new CustomAttribute(new MethodReference(".ctor", api.TypeSystem.Void, unresolved) { HasThis = true });
        action.CustomAttributes.Add(unresolvedAttribute);
        try { Offenders(modules).Should().Contain(value => value.StartsWith("Realm-role authorization metadata:", StringComparison.Ordinal)); }
        finally { action.CustomAttributes.Remove(unresolvedAttribute); }
        Offenders(modules).Should().BeEmpty("all mutations of actual production subjects are restored");
    });

    /// <summary>ADR-0064: approved owners do not authorize extra members, overloads or opaque exports.</summary>
    [Theory]
    [InlineData("verified-method")]
    [InlineData("verified-property")]
    [InlineData("verified-field")]
    [InlineData("verified-constructor")]
    [InlineData("identity-method")]
    [InlineData("identity-property")]
    [InlineData("identity-field")]
    [InlineData("identity-constructor")]
    [InlineData("feature-method")]
    [InlineData("feature-property")]
    [InlineData("feature-field")]
    [InlineData("feature-constructor")]
    [InlineData("witness-method")]
    [InlineData("witness-property")]
    [InlineData("witness-field")]
    [InlineData("witness-constructor")]
    [InlineData("candidate-opaque-forwarding")]
    [InlineData("generic-export")]
    [InlineData("object-export")]
    [InlineData("identity-constraint")]
    [InlineData("owner-overload")]
    public void Authority_census_closes_shape_and_approved_owner_exports(string mutation) => WithProduction(modules =>
    {
        Offenders(modules).Should().BeEmpty();
        var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
        var owner = mutation.StartsWith("witness-", StringComparison.Ordinal)
            ? api.GetType(CandidateSource).NestedTypes.Single(type => type.FullName == CredentialWitness)
            : api.GetType(mutation.StartsWith("feature-", StringComparison.Ordinal) ? Feature
                : mutation.StartsWith("verified-", StringComparison.Ordinal) ? Verified : Identity);
        var factory = api.GetType(Profile).Methods.Single(method => method.Name == "TryReadValidated");
        if (mutation == "candidate-opaque-forwarding")
        {
            var minter = api.GetType(CandidateSource).Methods.Single(method => method.Name == "ForValidatedScope");
            var forwarding = minter.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && target.DeclaringType.FullName == "LearnStack.SharedKernel.Permissions.AuthorizationCandidate/MintingSource"
                && target.Name == "ForCandidate");
            // Erasing any other object here must not inherit the one closed
            // private witness constructor's exemption.
            var construction = forwarding.Previous;
            var original = construction.Operand;
            construction.Operand = api.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
            Offenders(modules).Should().Contain(value => value.StartsWith("Opaque typed identity/feature export:", StringComparison.Ordinal));
            construction.Operand = original;
            Offenders(modules).Should().BeEmpty("restoring the exact witness construction restores the clean graph");
            return;
        }
        if (mutation.EndsWith("-property", StringComparison.Ordinal))
            owner.Properties.Add(new PropertyDefinition("Extra", PropertyAttributes.None, api.TypeSystem.String));
        else if (mutation.EndsWith("-field", StringComparison.Ordinal))
            owner.Fields.Add(new FieldDefinition("Extra", FieldAttributes.Private | FieldAttributes.InitOnly, api.TypeSystem.String));
        else if (mutation.EndsWith("-method", StringComparison.Ordinal))
            owner.Methods.Add(new MethodDefinition("Extra", MethodAttributes.Private, api.TypeSystem.Void));
        else if (mutation.EndsWith("-constructor", StringComparison.Ordinal))
        {
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, api.TypeSystem.Void);
            // Same arity, different signature, and actually called by the approved factory.
            foreach (var _ in Enumerable.Range(0, owner.FullName is Identity or Verified ? 5 : 0))
                constructor.Parameters.Add(new ParameterDefinition(api.TypeSystem.Object));
            owner.Methods.Add(constructor);
            factory.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, constructor));
        }
        else if (mutation == "owner-overload")
        {
            var overload = new MethodDefinition(factory.Name, MethodAttributes.Private | MethodAttributes.Static, api.TypeSystem.Void);
            overload.Parameters.Add(new ParameterDefinition(api.GetType(Identity)));
            overload.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            factory.DeclaringType.Methods.Add(overload);
        }
        else if (mutation == "identity-constraint")
        {
            var helper = new MethodDefinition("Constrained", MethodAttributes.Private | MethodAttributes.Static, api.TypeSystem.Void);
            var parameter = new GenericParameter("T", helper);
            parameter.Constraints.Add(new GenericParameterConstraint(api.GetType(Identity)));
            helper.GenericParameters.Add(parameter);
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            factory.DeclaringType.Methods.Add(helper);
            Offenders(modules).Should().NotBeEmpty("method-level identity constraints must be visible");
            factory.DeclaringType.Methods.Remove(helper);
            var genericType = new TypeDefinition("AuthorityProbe", "Constrained`1", TypeAttributes.Class, api.TypeSystem.Object);
            var typeParameter = new GenericParameter("T", genericType);
            typeParameter.Constraints.Add(new GenericParameterConstraint(api.GetType(Identity)));
            genericType.GenericParameters.Add(typeParameter);
            api.Types.Add(genericType);
        }
        else
        {
            using var probes = ModuleDefinition.ReadModule(typeof(AuthenticationAuthorityTests).Assembly.Location);
            var helper = probes.GetType(typeof(AuthenticationAuthorityTests).FullName).NestedTypes.Single(type => type.Name == nameof(AuthorityProbe))
                .Methods.Single(method => method.Name == nameof(AuthorityProbe.Format));
            MethodReference target = mutation == "generic-export"
                ? new GenericInstanceMethod(api.ImportReference(helper)) { GenericArguments = { api.GetType(Identity) } }
                : api.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!);
            factory.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
            if (target is GenericInstanceMethod generic)
            {
                Offenders(modules).Should().NotBeEmpty("generic identity formatting in an approved owner must be visible");
                generic.GenericArguments[0] = api.TypeSystem.Object;
            }
        }
        Offenders(modules).Should().NotBeEmpty("the real production graph must reject " + mutation);
    });

    /// <summary>ADR-0064: an approved caller cannot erase identity into an opaque helper argument.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Authority_census_rejects_actual_identity_forwarding_to_opaque_helpers(bool array)
        => WithProduction(modules =>
        {
            var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
            var helperType = new TypeDefinition("AuthorityProbe", "OpaqueExport", TypeAttributes.Abstract | TypeAttributes.Sealed, api.TypeSystem.Object);
            var helper = new MethodDefinition("Emit", MethodAttributes.Assembly | MethodAttributes.Static, api.TypeSystem.Void);
            helper.Parameters.Add(new ParameterDefinition(array ? new ArrayType(api.TypeSystem.Object) : api.TypeSystem.Object));
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            if (array)
            {
                helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldelem_Ref));
            }
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, api.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!)));
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Call, api.ImportReference(typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)])!)));
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            helperType.Methods.Add(helper);
            api.Types.Add(helperType);
            Offenders(modules).Should().BeEmpty("the helper alone carries no identity and is a clean control");

            var validated = api.GetType(Events).Methods.Single(method => method.Name == "TokenValidated");
            var setter = validated.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && Is(target.DeclaringType, Feature) && target.Name == "set_VerifiedSubject");
            var identity = validated.Body.Variables.Single(variable => Is(variable.VariableType, Verified));
            var transport = api.GetType(TransportSource).Methods.Single(method => method.Name == "Capture");
            var transportIdentity = transport.Body.Variables.Single(variable => Is(variable.VariableType, Identity));
            var identityStore = transport.Body.Instructions.Single(instruction => StoresLocal(instruction, transportIdentity));
            foreach (var (owner, insertion, projection) in new[]
                     { (validated, setter, identity), (transport, identityStore, transportIdentity) })
            {
                var forwarding = new List<Instruction>();
                if (array)
                    forwarding.AddRange([Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Newarr, api.TypeSystem.Object),
                        Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Ldc_I4_0)]);
                forwarding.Add(Instruction.Create(OpCodes.Ldloc, projection));
                if (array) forwarding.Add(Instruction.Create(OpCodes.Stelem_Ref));
                forwarding.Add(Instruction.Create(OpCodes.Call, helper));
                var processor = owner.Body.GetILProcessor();
                var previous = insertion;
                foreach (var instruction in forwarding)
                {
                    processor.InsertAfter(previous, instruction);
                    previous = instruction;
                }
                Offenders(modules).Should().Contain("Opaque typed identity/feature export: " + owner.FullName,
                    "the real approved caller forwarded its actual validated identity into " + (array ? "object[]" : "object"));
                foreach (var instruction in forwarding) processor.Remove(instruction);
                Offenders(modules).Should().BeEmpty("removing the forwarding restores the clean graph while retaining the helper");
            }
        });

    /// <summary>ADR-0064: erasing an identity through storage is an export even with a parameterless reader.</summary>
    [Theory]
    [InlineData("static")]
    [InlineData("instance")]
    [InlineData("array")]
    public void Authority_census_rejects_actual_identity_forwarding_through_opaque_storage(string storage)
        => WithProduction(modules =>
        {
            var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
            var type = new TypeDefinition("AuthorityProbe", "OpaqueStorage", TypeAttributes.Class, api.TypeSystem.Object);
            var field = new FieldDefinition("Value", FieldAttributes.Assembly | (storage == "instance" ? 0 : FieldAttributes.Static),
                storage == "array" ? new ArrayType(api.TypeSystem.Object) : api.TypeSystem.Object);
            type.Fields.Add(field);
            var constructor = new MethodDefinition(".ctor", MethodAttributes.Assembly | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, api.TypeSystem.Void);
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, api.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!)));
            constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(constructor);
            if (storage == "array")
            {
                var initialize = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, api.TypeSystem.Void);
                initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Newarr, api.TypeSystem.Object));
                initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, field));
                initialize.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                type.Methods.Add(initialize);
            }
            var helper = new MethodDefinition("Emit", MethodAttributes.Assembly | (storage == "instance" ? 0 : MethodAttributes.Static), api.TypeSystem.Void);
            if (storage == "instance") helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            helper.Body.Instructions.Add(Instruction.Create(storage == "instance" ? OpCodes.Ldfld : OpCodes.Ldsfld, field));
            if (storage == "array")
            {
                helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ldelem_Ref));
            }
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, api.ImportReference(typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!)));
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Call, api.ImportReference(typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)])!)));
            helper.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(helper);
            api.Types.Add(type);
            Offenders(modules).Should().BeEmpty("opaque storage and its parameterless reader carry no identity on their own");

            var validated = api.GetType(Events).Methods.Single(method => method.Name == "TokenValidated");
            var setter = validated.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
                && Is(target.DeclaringType, Feature) && target.Name == "set_VerifiedSubject");
            var identity = validated.Body.Variables.Single(variable => Is(variable.VariableType, Verified));
            var forwarding = new List<Instruction>();
            if (storage == "instance") forwarding.AddRange([Instruction.Create(OpCodes.Newobj, constructor), Instruction.Create(OpCodes.Dup)]);
            if (storage == "array") forwarding.AddRange([Instruction.Create(OpCodes.Ldsfld, field), Instruction.Create(OpCodes.Ldc_I4_0)]);
            forwarding.Add(Instruction.Create(OpCodes.Ldloc, identity));
            forwarding.Add(storage == "array" ? Instruction.Create(OpCodes.Stelem_Ref)
                : Instruction.Create(storage == "instance" ? OpCodes.Stfld : OpCodes.Stsfld, field));
            forwarding.Add(Instruction.Create(storage == "instance" ? OpCodes.Callvirt : OpCodes.Call, helper));
            var processor = validated.Body.GetILProcessor();
            var previous = setter;
            foreach (var instruction in forwarding)
            {
                processor.InsertAfter(previous, instruction);
                previous = instruction;
            }
            Offenders(modules).Should().Contain(value => value.StartsWith("Opaque typed identity/feature export:", StringComparison.Ordinal),
                "the approved TokenValidated caller stored its actual identity through " + storage);
            foreach (var instruction in forwarding) processor.Remove(instruction);
            Offenders(modules).Should().BeEmpty("removing identity storage restores the clean graph while retaining the reader");
        });

    private static class AuthorityProbe
    {
        internal static string Format<T>(T value) => $"{value}";
        internal static bool Role(ClaimsPrincipal principal) => principal.IsInRole("tenant-admin");
        internal static bool InterfaceRole(System.Security.Principal.IPrincipal principal) => principal.IsInRole("tenant-admin");
        internal static string? IdentityName(System.Security.Principal.IIdentity identity) => identity.Name;
        internal static Claim? Claim(ClaimsPrincipal principal) => principal.FindFirst("permission");
        internal static bool ClaimSet(ClaimsPrincipal principal) => principal.HasClaim("scope", "write");
        internal static string Email(Claim claim) => claim.Value;
        internal static bool Authenticated(System.Security.Principal.IIdentity identity) => identity.IsAuthenticated;
        internal static ClaimsPrincipal RawUser(HttpContext context) => context.User;
        internal static ClaimsPrincipal? AuthenticatedPrincipal(Microsoft.AspNetCore.Authentication.AuthenticateResult result) => result.Principal;
        internal static ClaimsPrincipal TicketPrincipal(Microsoft.AspNetCore.Authentication.AuthenticationTicket ticket) => ticket.Principal;
        internal static Task<AuthorizationResult> AuthorizePolicy(IAuthorizationService service, ClaimsPrincipal principal) =>
            service.AuthorizeAsync(principal, null, "tenant-admin");
        internal static Task<AuthorizationResult> AuthorizeRequirements(IAuthorizationService service, ClaimsPrincipal principal) =>
            service.AuthorizeAsync(principal, null, Array.Empty<IAuthorizationRequirement>());
        internal static AuthorizationPolicyBuilder RolePolicy(AuthorizationPolicyBuilder builder) => builder.RequireRole("tenant-admin");
        internal static AuthorizationPolicyBuilder ClaimPolicy(AuthorizationPolicyBuilder builder) => builder.RequireClaim("permission", "write");
        internal static IAuthorizeData InterfaceRoleMetadata(IAuthorizeData metadata)
        {
            metadata.Roles = "tenant-admin";
            return metadata;
        }
        internal static object ValidationClaims(Microsoft.IdentityModel.Tokens.TokenValidationResult result) => result.Claims;
        internal static Microsoft.IdentityModel.Tokens.SecurityToken ValidatedSecurityToken(Microsoft.IdentityModel.Tokens.TokenValidationResult result) => result.SecurityToken;
        internal static bool RawTokenClaim(Microsoft.IdentityModel.JsonWebTokens.JsonWebToken token) => token.GetPayloadValue<string>("permission") == "write";
        internal static bool RawTokenSubject(Microsoft.IdentityModel.JsonWebTokens.JsonWebToken token) => token.Subject == "tenant-admin";
        internal static bool LegacyTokenPayload(System.IdentityModel.Tokens.Jwt.JwtSecurityToken token) => (string)token.Payload["permission"] == "write";
        internal static bool RawHeaderReparse(HttpContext context) => new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(
            context.Request.Headers.Authorization.ToString()["Bearer ".Length..]).GetPayloadValue<string>("permission") == "write";
        internal static ValidatedTenantIdentity Forge() => new("issuer", "subject", UserId.From(Guid.Parse("a2999254-8355-43da-86ea-5cbebf105d29")), null, null);
        internal static VerifiedProviderSubject ForgeVerified() => new("issuer", "subject", null, null, null);
        internal static VerifiedProviderSubject CloneVerified(VerifiedProviderSubject subject) => subject with { Subject = "substitution" };
        internal static string VerifiedText(VerifiedProviderSubject subject) => subject.ToString();
        internal static TenantCredentialFeature ForgeFeature(ValidatedTenantIdentity identity) => new() { Identity = identity };
        internal static ValidatedTenantIdentity Clone(ValidatedTenantIdentity identity) => identity with { Subject = "tenant-admin" };
        internal static bool SubjectPermission(ValidatedTenantIdentity identity) => identity.Subject == "tenant-admin";
        internal static bool SubjectDeconstruct(ValidatedTenantIdentity identity)
        {
            var (_, subject, _, _, _) = identity;
            return subject == "tenant-admin";
        }
        internal static bool SubjectText(ValidatedTenantIdentity identity) => identity.ToString().Contains("Subject = tenant-admin", StringComparison.Ordinal);
        internal static bool SubjectInterpolation(ValidatedTenantIdentity identity) => $"{identity}".Contains("Subject = tenant-admin", StringComparison.Ordinal);
        internal static bool FeatureFormatting(HttpContext context) => $"{context.Features.Get<TenantCredentialFeature>()?.Identity}"
            .Contains("Subject = tenant-admin", StringComparison.Ordinal);
        internal static bool TypedActor(UserId user) => user == UserId.SystemActor;
    }
}
