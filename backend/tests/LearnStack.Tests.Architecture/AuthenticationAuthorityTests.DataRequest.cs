using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0106 § 9: the retained own data-request entry's authority exceptions, each the consent subject's exception
/// applied to the data-request source and credential, and each refusing a substituted value.
/// </summary>
public sealed partial class AuthenticationAuthorityTests
{
    private const string DataRequestInput = Authentication + "OwnDataRequestHttpInput";
    private const string DataRequestEntryRequest = Authentication + "OwnDataRequestEntryRequest";
    private const string DataRequestWitnessSource = "LearnStack.SharedKernel.Identity.OwnDataRequestScopeWitness/MintingSource";
    private static readonly string[] DataRequestResolverActorSubstitutions = ["identity!.ActorUserId", "UserId.SystemActor"];
    private static readonly string DataRequestSourceText = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
        "LearnStack.Api/Authentication/OwnDataRequestSubjectSource.cs"));
    private static readonly bool DataRequestSourceIsConfined = DataRequestForwardingSourceIsConfined(DataRequestSourceText);

    // The same immediate hash sink as the consent credential: no token storage, claim read or forwarding.
    private static bool IsClosedDataRequestTokenRead(Call call, Call[] calls)
    {
        const string encoded = "System.String Microsoft.IdentityModel.JsonWebTokens.JsonWebToken::get_EncodedToken()";
        if (call.Source.Site != ApprovedSites[DataRequestCredential + ".Capture"] || call.Target.FullName != encoded) return false;
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
            && calls.Count(candidate => candidate.Target.FullName == ApprovedSites[DataRequestCredential + ".Capture"]) == 1
            && calls.Count(candidate => candidate.Target.FullName == ApprovedSites[DataRequestCredential + ".Capture"]
                && candidate.Source.Site == ApprovedSites[Events + ".TokenValidated"]) == 1;
    }

    private static bool IsCapturedDataRequestRead(Instruction? read) => read is { OpCode.Code: Code.Callvirt, Operand: MethodReference getter }
        && getter.FullName == Signature(DataRequestInput, "get_Request", "System.Object");

    private static bool IsClosedDataRequestRequestStore(Method method, Instruction instruction) => DataRequestSourceIsConfined
        && method.Site == ApprovedSites[DataRequestSource + ".PrepareAsync"] && instruction.OpCode == OpCodes.Stfld
        && instruction.Operand is FieldReference field && field.DeclaringType.FullName == DataRequestSource && field.Name == "_request"
        && IsCapturedDataRequestRead(instruction.Previous);

    private static bool IsClosedDataRequestForwarding(Method method, Instruction instruction, MethodReference target)
    {
        if (!DataRequestSourceIsConfined) return false;
        if (method.Site == ApprovedSites[DataRequestSource + ".ForInvocation"])
        {
            if (target.FullName == Signature(DataRequestWitnessSource, "IsEvidenceCurrent", "System.Boolean", "System.Object", "LearnStack.SharedKernel.Permissions.OperationInvocation"))
                return instruction.Previous?.OpCode == OpCodes.Ldarg_1 && instruction.Previous.Previous?.OpCode == OpCodes.Ldarg_0
                    && instruction.Previous.Previous.Previous?.OpCode == OpCodes.Ldarg_0;
            return target.FullName == Signature(DataRequestWitnessSource, "Capture", "LearnStack.SharedKernel.Identity.OwnDataRequestScopeWitness",
                "System.Object", "LearnStack.SharedKernel.Permissions.OperationInvocation", "LearnStack.SharedKernel.Identity.GlobalIdentitySubject", User,
                "LearnStack.SharedKernel.Identifiers.TenantId", Organization, "System.String", "LearnStack.SharedKernel.Identity.OwnDataRequestPurpose",
                "LearnStack.SharedKernel.DataProtection.DataRequestScopeGroup", "LearnStack.SharedKernel.Identity.OwnDataRequestScopePage")
                && method.Definition.Body.Instructions.Count(candidate => candidate.Operand is MethodReference capture
                    && capture.FullName == target.FullName) == 1
                && DataRequestCapturePrefix(method) is { } prefix && prefix.OpCode == OpCodes.Ldarg_0
                && prefix.Next?.OpCode == OpCodes.Ldarg_0 && prefix.Next.Next?.OpCode == OpCodes.Ldarg_1;
        }
        // Preparation forwards one opaque value: the captured input's request, into its request feature.
        return method.Site == ApprovedSites[DataRequestSource + ".PrepareAsync"]
            && target.FullName == Signature(DataRequestEntryRequest, ".ctor", "System.Void", "System.Object")
            && instruction.OpCode == OpCodes.Newobj && IsCapturedDataRequestRead(instruction.Previous);
    }

    private static Instruction? DataRequestCapturePrefix(Method method)
    {
        var subject = method.Definition.Body.Instructions.SingleOrDefault(instruction => instruction.OpCode == OpCodes.Ldfld
            && instruction.Operand is FieldReference field && field.DeclaringType.FullName == DataRequestSource && field.Name == "_subject");
        return subject?.Previous?.Previous?.Previous?.Previous;
    }

    private static bool DataRequestForwardingSourceIsConfined(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "OwnDataRequestSubjectSource");
        if (type is null) return false;
        var prepare = type.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "PrepareAsync");
        var invocation = type.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(node => node.Identifier.ValueText == "ForInvocation");
        if (prepare is null || invocation is null) return false;
        var assignments = prepare.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(node => node.Left.ToString() is "_request" or "_requestFeature").ToArray();
        var lambdas = prepare.DescendantNodes().OfType<LambdaExpressionSyntax>().ToArray();
        // Every scope observation is for the normalized actor under the independently classified host ceiling.
        var lookups = prepare.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(node => node.Expression.ToString() == "lookup.LookupAsync").ToArray();
        var mint = invocation.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(node => node.Expression.ToString() is "Capture" or "IsEvidenceCurrent").ToArray();
        return assignments.Length == 2 && Equivalent(assignments[0], "_request = input.Request")
            && Equivalent(assignments[1], "_requestFeature = new OwnDataRequestEntryRequest(input.Request)")
            && lambdas.Length == 0
            && lookups.Length == 3 && lookups.All(call => call.ArgumentList.Arguments.Count == 8
                && call.ArgumentList.Arguments[0].ToString() == "identity.ActorUserId"
                && call.ArgumentList.Arguments[1].ToString() == "tenant"
                && call.ArgumentList.Arguments[2].ToString() == "host.OrganizationId")
            && mint.Length == 2 && Equivalent(mint[0], "IsEvidenceCurrent(this, invocation)")
            && Equivalent(mint[1], "Capture(this, invocation, _subject!, _identity!.ActorUserId, _host!.TenantId!.Value, _host.OrganizationId, _host.Host, _purpose!.Purpose, Selected, _page)");

        static bool Equivalent(Microsoft.CodeAnalysis.SyntaxNode actual, string expected) =>
            SyntaxFactory.AreEquivalent(actual, SyntaxFactory.ParseExpression(expected));
    }

    private static bool DataRequestResolverActorIsConfined(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root.ContainsDiagnostics) return false;
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(node =>
            node.Expression.ToString() == "TenantContextFactory.CreateForOwnDataRequestScope").ToArray();
        return calls.Length == 1 && calls[0].Ancestors().OfType<MethodDeclarationSyntax>().Single().Identifier.ValueText == "InvokeAsync"
            && SyntaxFactory.AreEquivalent(calls[0], SyntaxFactory.ParseExpression(
                "TenantContextFactory.CreateForOwnDataRequestScope(classification.TenantId!.Value, classification.OrganizationId, dataRequestIdentity.ActorUserId, source.Selected, Activity.Current?.Id ?? context.TraceIdentifier)"));
    }

    [Fact]
    public void Retained_data_request_authority_exceptions_reject_substituted_values() => WithProduction(modules =>
    {
        Offenders(modules).Should().BeEmpty();
        DataRequestSourceIsConfined.Should().BeTrue();
        var resolverSource = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, "LearnStack.Api/Tenancy/TenantResolverMiddleware.cs"));
        DataRequestResolverActorIsConfined(resolverSource).Should().BeTrue();
        foreach (var replacement in DataRequestResolverActorSubstitutions)
        {
            var dirty = resolverSource.Replace("dataRequestIdentity.ActorUserId", replacement, StringComparison.Ordinal);
            dirty.Should().NotBe(resolverSource);
            DataRequestResolverActorIsConfined(dirty).Should().BeFalse("the retained scope uses the completed normalized actor on the real host");
        }

        foreach (var (before, after) in new[]
        {
            ("_request = input.Request", "_request = identity"),
            ("new OwnDataRequestEntryRequest(input.Request)", "new OwnDataRequestEntryRequest(identity)"),
            ("IsEvidenceCurrent(this, invocation)", "IsEvidenceCurrent(_identity!, invocation)"),
            ("Capture(this, invocation, _subject!", "Capture(_identity!, invocation, _subject!"),
            ("lookup.LookupAsync(identity.ActorUserId, tenant, host.OrganizationId", "lookup.LookupAsync(UserId.SystemActor, tenant, host.OrganizationId"),
            ("lookup.LookupAsync(identity.ActorUserId, tenant, host.OrganizationId", "lookup.LookupAsync(identity.ActorUserId, tenant, input.OrganizationId"),
        })
        {
            var dirty = DataRequestSourceText.Replace(before, after, StringComparison.Ordinal);
            dirty.Should().NotBe(DataRequestSourceText);
            DataRequestForwardingSourceIsConfined(dirty).Should().BeFalse("no source exception may forward identity, widen the host or replace the captured request");
        }
        var lambda = DataRequestSourceText.Replace("var input = await OwnDataRequestHttpInput.ReadAsync(http, purpose);",
            "var input = await OwnDataRequestHttpInput.ReadAsync(http, purpose); Func<object> leak = () => identity;", StringComparison.Ordinal);
        lambda.Should().NotBe(DataRequestSourceText);
        DataRequestForwardingSourceIsConfined(lambda).Should().BeFalse("preparation crosses no delegate boundary");

        var api = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Api");
        var methods = Methods(api.Types.SelectMany(Flatten)).ToArray();
        foreach (var site in new[] { DataRequestSource + ".PrepareAsync", DataRequestSource + ".ForInvocation", DataRequestSource + ".get_Actor",
                     DataRequestSource + ".DownloadCredential", ExportBinding + ".CaptureRetained" })
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
        var capture = methods.Single(method => method.Site == ApprovedSites[DataRequestCredential + ".Capture"]);
        var read = capture.Definition.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.FullName == "System.String Microsoft.IdentityModel.JsonWebTokens.JsonWebToken::get_EncodedToken()");
        Check(read.Previous, OpCodes.Ldnull, null, "the encoded token comes from the validated Capture argument");
        Check(read.Next.Next, OpCodes.Nop, null, "the encoded token cannot escape its immediate hash sink");
        var extraRead = Instruction.Create(OpCodes.Callvirt, (MethodReference)read.Operand);
        capture.Definition.Body.Instructions.Add(extraRead);
        Offenders(modules).Should().NotBeEmpty("an extra encoded-token read is not covered");
        capture.Definition.Body.Instructions.Remove(extraRead);
        var prepare = methods.Single(method => method.Site == ApprovedSites[DataRequestSource + ".PrepareAsync"] && method.Definition.Name == "MoveNext");
        foreach (var sink in prepare.Definition.Body.Instructions.Where(instruction =>
                     instruction.Operand is FieldReference field && field.DeclaringType.FullName == DataRequestSource && field.Name == "_request" && instruction.OpCode == OpCodes.Stfld
                     || instruction.Operand is MethodReference target && target.FullName == Signature(DataRequestEntryRequest, ".ctor", "System.Void", "System.Object")).ToArray())
            Check(sink.Previous, OpCodes.Ldnull, null, "only the captured request getter supplies each opaque request sink");
        var invocation = methods.Single(method => method.Site == ApprovedSites[DataRequestSource + ".ForInvocation"]);
        var prefix = DataRequestCapturePrefix(invocation)!;
        Check(prefix.Next, OpCodes.Ldnull, null, "the data-request minter accepts only its own captured evidence object");
        var current = invocation.Definition.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == DataRequestWitnessSource && target.Name == "IsEvidenceCurrent");
        Check(current.Previous.Previous, OpCodes.Ldnull, null, "the data-request liveness gate checks only this exact source");
        var witness = api.GetType(CandidateSource).NestedTypes.Single(type => type.FullName == CredentialWitness);
        var field = witness.Fields.Single(item => item.Name == "<DataRequest>k__BackingField");
        field.IsInitOnly = false;
        Offenders(modules).Should().NotBeEmpty("the tenth witness field is immutable");
        field.IsInitOnly = true;
        var property = witness.Properties.Single(item => item.Name == "DataRequest");
        witness.Properties.Remove(property);
        Offenders(modules).Should().NotBeEmpty("the tenth witness property is a required exact subject");
        witness.Properties.Add(property);
        var constructor = witness.Methods.Single(method => method.IsConstructor);
        var parameter = constructor.Parameters[^1];
        var originalType = parameter.ParameterType;
        parameter.ParameterType = api.TypeSystem.Object;
        Offenders(modules).Should().NotBeEmpty("the tenth constructor argument cannot erase the data-request source type");
        parameter.ParameterType = originalType;
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
