using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticationAuthorityTests
{
    private const string ConsentInput = Authentication + "ConsentSubjectHttpInput";
    private const string ConsentRequest = Authentication + "ConsentSubjectRequest";
    private const string ConsentWitnessSource = "LearnStack.SharedKernel.Identity.ConsentSubjectScopeWitness/MintingSource";
    private static readonly string[] ConsentResolverActorSubstitutions = ["identity!.ActorUserId", "UserId.SystemActor"];
    private static readonly string ConsentSourceText = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
        "LearnStack.Api/Authentication/ConsentSubjectSource.cs"));
    private static readonly bool ConsentSourceIsConfined = ConsentForwardingSourceIsConfined(ConsentSourceText);

    // This exact stock-validated token read is immediately hashed and compared
    // against pending validation. No token storage, claim read or forwarding is admitted.
    private static bool IsClosedConsentTokenRead(Call call, Call[] calls)
    {
        const string encoded = "System.String Microsoft.IdentityModel.JsonWebTokens.JsonWebToken::get_EncodedToken()";
        if (call.Source.Site != ApprovedSites[ConsentCredential + ".Capture"] || call.Target.FullName != encoded) return false;
        var reads = call.Source.Definition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
            && target.FullName == encoded).ToArray();
        if (reads.Length != 1 || reads[0].OpCode != OpCodes.Callvirt) return false;
        var read = reads[0];
        return read.Previous?.OpCode == OpCodes.Ldarg_1
            && read.Next?.Operand is MethodReference bytes
            && bytes.FullName == "System.Byte[] System.Text.Encoding::GetBytes(System.String)"
            && read.Next.Next?.Operand is MethodReference hash
            && hash.FullName == "System.Byte[] System.Security.Cryptography.SHA256::HashData(System.Byte[])"
            && read.Next.Next.Next?.Next?.Operand is MethodReference compare
            && compare.FullName == "System.Boolean System.Security.Cryptography.CryptographicOperations::FixedTimeEquals(System.ReadOnlySpan`1<System.Byte>,System.ReadOnlySpan`1<System.Byte>)"
            && calls.Count(candidate => candidate.Target.FullName == ApprovedSites[ConsentCredential + ".Capture"]) == 1
            && calls.Count(candidate => candidate.Target.FullName == ApprovedSites[ConsentCredential + ".Capture"]
                && candidate.Source.Site == ApprovedSites[Events + ".TokenValidated"]) == 1;
    }

    private static bool IsCapturedConsentRequestRead(Instruction? read) => read is { OpCode.Code: Code.Callvirt, Operand: MethodReference getter }
        && getter.FullName == Signature(ConsentInput, "get_Request", "System.Object");

    private static bool IsClosedConsentRequestStore(Method method, Instruction instruction) => ConsentSourceIsConfined
        && method.Site == ApprovedSites[ConsentSource + ".PrepareAsync"] && instruction.OpCode == OpCodes.Stfld
        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == ConsentSource && field.Name == "_request"
        && IsCapturedConsentRequestRead(instruction.Previous);

    private static bool IsClosedConsentForwarding(Method method, Instruction instruction, MethodReference target)
    {
        if (!ConsentSourceIsConfined) return false;
        if (method.Site == ApprovedSites[ConsentSource + ".ForInvocation"])
        {
            if (target.FullName == Signature(ConsentWitnessSource, "IsEvidenceCurrent", "System.Boolean", "System.Object", "LearnStack.SharedKernel.Permissions.OperationInvocation"))
                return instruction.Previous?.OpCode == OpCodes.Ldarg_1 && instruction.Previous.Previous?.OpCode == OpCodes.Ldarg_0
                    && instruction.Previous.Previous.Previous?.OpCode == OpCodes.Ldarg_0;
            // Source syntax fixes Capture's opaque first argument to this, with
            // every remaining argument taken from the already captured evidence.
            return target.FullName == Signature(ConsentWitnessSource, "Capture", "LearnStack.SharedKernel.Identity.ConsentSubjectScopeWitness",
                "System.Object", "LearnStack.SharedKernel.Permissions.OperationInvocation", "LearnStack.SharedKernel.Identity.GlobalIdentitySubject", User,
                "LearnStack.SharedKernel.Identifiers.TenantId", Organization, "System.String", "LearnStack.SharedKernel.Identity.ConsentSubjectPurpose",
                "LearnStack.SharedKernel.Identity.ConsentSubjectScopeObservation", "LearnStack.SharedKernel.Identity.ConsentSubjectScopePage")
                && method.Definition.Body.Instructions.Count(candidate => candidate.Operand is MethodReference capture
                    && capture.FullName == target.FullName) == 1
                && ConsentCapturePrefix(method) is { } prefix && prefix.OpCode == OpCodes.Ldarg_0
                && prefix.Next?.OpCode == OpCodes.Ldarg_0 && prefix.Next.Next?.OpCode == OpCodes.Ldarg_1;

        }
        if (method.Site != ApprovedSites[ConsentSource + ".PrepareAsync"]) return false;
        if (target.FullName == Signature(ConsentRequest, ".ctor", "System.Void", "System.Object"))
            return instruction.OpCode == OpCodes.Newobj && IsCapturedConsentRequestRead(instruction.Previous);
        // The only two delegate targets are the fixed retained-scope validation
        // and Membership-ID projection. Their source bodies are checked below;
        // the receiver may carry only the independently classified host/tenant.
        if (instruction.OpCode != OpCodes.Newobj || instruction.Previous is not { OpCode.Code: Code.Ldftn, Operand: MethodReference function }
            || target.FullName is not ("System.Void System.Func`2<LearnStack.SharedKernel.Identity.ConsentSubjectScopeObservation,System.Boolean>::.ctor(System.Object,System.IntPtr)"
                or "System.Void System.Func`2<LearnStack.SharedKernel.Identity.ConsentSubjectScopeObservation,System.Guid>::.ctor(System.Object,System.IntPtr)")) return false;
        var definition = function.Resolve();
        return definition?.DeclaringType.DeclaringType?.FullName == ConsentSource
            && definition.Name.Contains("<PrepareAsync>", StringComparison.Ordinal)
            && definition.DeclaringType.Fields.Where(field => !field.IsStatic).All(field => field.FieldType.FullName is
                "LearnStack.SharedKernel.Identifiers.TenantId" or Host);
    }

    private static Instruction? ConsentCapturePrefix(Method method)
    {
        // The first typed subject argument follows receiver, exact evidence=this,
        // invocation and this._subject. Anchor the opaque argument to that field.
        var subject = method.Definition.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode == OpCodes.Ldfld
            && instruction.Operand is FieldReference field && field.DeclaringType.FullName == ConsentSource && field.Name == "_subject");
        return subject?.Previous?.Previous?.Previous?.Previous;
    }

    private static bool ConsentForwardingSourceIsConfined(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "ConsentSubjectSource");
        if (type is null) return false;
        var prepare = type.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "PrepareAsync");
        var invocation = type.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "ForInvocation");
        if (prepare is null || invocation is null) return false;
        var assignments = prepare.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(node => node.Left.ToString() is "_request" or "_requestFeature").ToArray();
        var lambdas = prepare.DescendantNodes().OfType<SimpleLambdaExpressionSyntax>().ToArray();
        var mint = invocation.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(node => node.Expression.ToString() is "Capture" or "IsEvidenceCurrent").ToArray();
        return assignments.Length == 2 && Equivalent(assignments[0], "_request = input.Request")
            && Equivalent(assignments[1], "_requestFeature = new ConsentSubjectRequest(input.Request)")
            && lambdas.Length == 2
            && Equivalent(lambdas[0], "item => item.MembershipId == Guid.Empty || item.MembershipId.Version != 7 || item.TenantId != tenant || host.OrganizationId is { } branch && item.OrganizationId != branch")
            && Equivalent(lambdas[1], "item => item.MembershipId")
            && mint.Length == 2 && Equivalent(mint[0], "IsEvidenceCurrent(this, invocation)")
            && Equivalent(mint[1], "Capture(this, invocation, _subject!, _identity!.ActorUserId, _host!.TenantId!.Value, _host.OrganizationId, _host.Host, _purpose!.Purpose, Selected, _page)");

        static bool Equivalent(Microsoft.CodeAnalysis.SyntaxNode actual, string expected) =>
            SyntaxFactory.AreEquivalent(actual, SyntaxFactory.ParseExpression(expected));
    }

    private static bool ConsentResolverActorIsConfined(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(node =>
            node.Expression.ToString() == "TenantContextFactory.CreateForConsentSubjectScope").ToArray();
        return calls.Length == 1 && calls[0].Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText == "InvokeAsync"
            && SyntaxFactory.AreEquivalent(calls[0], SyntaxFactory.ParseExpression(
                "TenantContextFactory.CreateForConsentSubjectScope(classification.TenantId!.Value, classification.OrganizationId, consentIdentity.ActorUserId, source.Selected, Activity.Current?.Id ?? context.TraceIdentifier)"));
    }

    [Fact]
    public void Consent_authority_exceptions_reject_substituted_values() => WithProduction(modules =>
    {
        Offenders(modules).Should().BeEmpty();
        ConsentSourceIsConfined.Should().BeTrue();
        var resolverSource = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Api/Tenancy/TenantResolverMiddleware.cs"));
        ConsentResolverActorIsConfined(resolverSource).Should().BeTrue();
        foreach (var replacement in ConsentResolverActorSubstitutions)
        {
            var dirty = resolverSource.Replace("consentIdentity.ActorUserId", replacement, StringComparison.Ordinal);
            dirty.Should().NotBe(resolverSource);
            ConsentResolverActorIsConfined(dirty).Should().BeFalse("consent scope uses the completed normalized actor on the real host");
        }

        foreach (var (before, after) in new[]
        {
            ("_request = input.Request", "_request = identity"),
            ("new ConsentSubjectRequest(input.Request)", "new ConsentSubjectRequest(identity)"),
            ("IsEvidenceCurrent(this, invocation)", "IsEvidenceCurrent(_identity!, invocation)"),
            ("Capture(this, invocation, _subject!", "Capture(_identity!, invocation, _subject!"),
            ("item => item.MembershipId)", "item => identity.ActorUserId.Value)"),
            ("item.TenantId != tenant", "item.TenantId != identity.TenantId"),
        })
        {
            var dirty = ConsentSourceText.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(ConsentSourceText);
            ConsentForwardingSourceIsConfined(dirty).Should().BeFalse("no source exception may forward identity or replace the captured request/host");
        }
        var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
        var methods = Methods(api.Types.SelectMany(Flatten)).ToArray();
        foreach (var site in new[] { Resolver + ".InvokeAsync", ConsentSource + ".PrepareAsync", ConsentSource + ".ForInvocation", ConsentSource + ".get_Actor" })
        {
            var actorReads = methods.Where(method => method.Site == ApprovedSites[site]).SelectMany(method => method.Definition.Body.Instructions)
                .Where(instruction => instruction.Operand is MethodReference target && target.FullName == Signature(Identity, "get_ActorUserId", User))
                .ToArray();
            actorReads.Should().NotBeEmpty(site);
            var saved = actorReads.Select(instruction => (Instruction: instruction, instruction.OpCode, instruction.Operand)).ToArray();
            foreach (var item in saved) { item.Instruction.OpCode = OpCodes.Nop; item.Instruction.Operand = null; }
            Offenders(modules).Should().Contain(error => error.StartsWith("Changed/missing validated ActorUserId consumers:", StringComparison.Ordinal));
            foreach (var item in saved) { item.Instruction.OpCode = item.OpCode; item.Instruction.Operand = item.Operand; }
            Offenders(modules).Should().BeEmpty("restoring " + site + " restores its exact normalized actor consumer");
        }
        var capture = methods.Single(method => method.Site == ApprovedSites[ConsentCredential + ".Capture"]);
        var read = capture.Definition.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.FullName == "System.String Microsoft.IdentityModel.JsonWebTokens.JsonWebToken::get_EncodedToken()");
        Check(read.Previous, OpCodes.Ldnull, null, "the encoded token comes from the validated Capture argument");
        Check(read.Next.Next, OpCodes.Nop, null,
            "the encoded token cannot escape its immediate hash sink");
        var extraRead = Instruction.Create(OpCodes.Callvirt, (MethodReference)read.Operand);
        capture.Definition.Body.Instructions.Add(extraRead);
        Offenders(modules).Should().NotBeEmpty("an extra encoded-token read is not covered");
        capture.Definition.Body.Instructions.Remove(extraRead);
        var prepare = methods.Single(method => method.Site == ApprovedSites[ConsentSource + ".PrepareAsync"] && method.Definition.Name == "MoveNext");
        foreach (var sink in prepare.Definition.Body.Instructions.Where(instruction =>
                     instruction.Operand is FieldReference field && field.DeclaringType.FullName == ConsentSource && field.Name == "_request" && instruction.OpCode == OpCodes.Stfld
                     || instruction.Operand is MethodReference target && target.FullName == Signature(ConsentRequest, ".ctor", "System.Void", "System.Object")).ToArray())
            Check(sink.Previous, OpCodes.Ldnull, null, "only the captured request getter supplies each opaque request sink");
        var invocation = methods.Single(method => method.Site == ApprovedSites[ConsentSource + ".ForInvocation"]);
        var prefix = ConsentCapturePrefix(invocation)!;
        Check(prefix.Next, OpCodes.Ldnull, null, "the consent minter accepts only its own captured evidence object");
        var current = invocation.Definition.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == ConsentWitnessSource && target.Name == "IsEvidenceCurrent");
        Check(current.Previous.Previous, OpCodes.Ldnull, null, "the consent liveness gate checks only this exact source");
        foreach (var function in prepare.Definition.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Newobj
                     && instruction.Operand is MethodReference target && target.DeclaringType.FullName.StartsWith("System.Func`2<LearnStack.SharedKernel.Identity.ConsentSubjectScopeObservation,", StringComparison.Ordinal)).ToArray())
            Check(function.Previous, OpCodes.Ldnull, null, "only the two confined retained-scope lambdas may cross the delegate boundary");
        var witness = api.GetType(CandidateSource).NestedTypes.Single(type => type.FullName == CredentialWitness);
        var consent = witness.Fields.Single(field => field.Name == "<Consent>k__BackingField");
        consent.IsInitOnly = false;
        Offenders(modules).Should().NotBeEmpty("the ninth witness field is immutable");
        consent.IsInitOnly = true;
        var property = witness.Properties.Single(item => item.Name == "Consent");
        witness.Properties.Remove(property);
        Offenders(modules).Should().NotBeEmpty("the ninth witness property is a required exact subject");
        witness.Properties.Add(property);
        var constructor = witness.Methods.Single(method => method.IsConstructor);
        var parameter = constructor.Parameters[^1];
        var originalType = parameter.ParameterType;
        parameter.ParameterType = api.TypeSystem.Object;
        Offenders(modules).Should().NotBeEmpty("the ninth constructor argument cannot erase the consent source type");
        parameter.ParameterType = originalType;
        var text = witness.Methods.Single(method => method.Name == "ToString").Body.Instructions.Single(instruction => instruction.OpCode == OpCodes.Ldstr);
        Check(text, OpCodes.Ldstr, "Consent: sensitive", "the witness keeps its exact nonformatting representation");
        Offenders(modules).Should().BeEmpty();

        void Check(Instruction instruction, OpCode opcode, object? operand, string because)
        {
            var originalOpcode = instruction.OpCode; var originalOperand = instruction.Operand;
            instruction.OpCode = opcode; instruction.Operand = operand;
            Offenders(modules).Should().NotBeEmpty(because);
            instruction.OpCode = originalOpcode; instruction.Operand = originalOperand;
            Offenders(modules).Should().BeEmpty("restoring " + because);
        }
    });
}
