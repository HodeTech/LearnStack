using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Infrastructure.Email;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Infrastructure.Invitations;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class InvitationSmtpBoundaryTests
{
    [Fact]
    public void Invitation_Email_Custody_Is_Closed_And_Owned()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(JobExecutor).Assembly.Location);
        var types = assembly.MainModule.Types.SelectMany(Flatten).ToArray();
        var mint = Calls(types, "LearnStack.Infrastructure.Email.InvitationSmtpExecutionReceipt", ".ctor");
        mint.Should().ContainSingle();
        mint.Single().DeclaringType.FullName.Should().StartWith("LearnStack.Infrastructure.Jobs.JobExecutor/<InvokeExternalAsync>");
        Calls(types, "LearnStack.Infrastructure.Email.InvitationSmtpProviderDataSource", "OpenAsync")
            .Should().ContainSingle(method => method.DeclaringType.FullName.StartsWith("LearnStack.Infrastructure.Email.InvitationSmtpDelivery/<ObserveAsync>", StringComparison.Ordinal));
        var source = types.Single(type => type.FullName == typeof(InvitationSmtpProviderDataSource).FullName);
        source.Fields.Should().OnlyContain(field => field.IsPrivate);
        source.Methods.Where(method => method.IsPublic).SelectMany(method => method.Parameters.Select(parameter => parameter.ParameterType.FullName).Append(method.ReturnType.FullName))
            .Should().NotContain(name => name.Contains("Npgsql", StringComparison.Ordinal) || name.Contains("Func`", StringComparison.Ordinal));
        var owner = types.Single(type => type.FullName == "LearnStack.Infrastructure.Email.InvitationSmtpProviderDataSource/Owner");
        var open = owner.NestedTypes.Single(type => type.Name.StartsWith("<OpenPhysicalAsync>", StringComparison.Ordinal)).Methods.Single(method => method.Name == "MoveNext");
        open.Body.Instructions.Where(instruction => instruction.Operand is MethodReference called
            && called.DeclaringType.FullName == "LearnStack.Infrastructure.Persistence.ApplicationDataSource" && called.Name == "RefuseBypassRoleAsync")
            .Should().ContainSingle();
        Calls(types, owner.FullName, "RetainUnconfirmedSmtp").Should().ContainSingle(method =>
            method.DeclaringType.FullName.StartsWith("LearnStack.Infrastructure.Email.InvitationSmtpDelivery/<ObserveAsync>", StringComparison.Ordinal));
        assembly.MainModule.AssemblyReferences.Should().NotContain(reference => reference.Name.StartsWith("LearnStack.Modules.", StringComparison.Ordinal));
    }

    [Fact]
    public void Contract_and_adapter_have_only_the_fixed_external_operation_and_no_application_access()
    {
        typeof(IInvitationEmailOperation).GetInterfaces().Should().ContainSingle();
        typeof(IInvitationEmailOperation).GetMethods().Should().BeEmpty();
        var adapter = typeof(InvitationSmtpOperation);
        adapter.GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(InvitationSmtpDelivery));
        typeof(InvitationEmailParameters).GetProperties().Should().OnlyContain(property => property.PropertyType == typeof(Guid));
        typeof(InvitationEmailPlan).GetProperties().Should().OnlyContain(property => property.PropertyType == typeof(Guid));
        typeof(InvitationEmailOutcome).GetProperties().Should().OnlyContain(property => property.PropertyType == typeof(Guid));
    }

    [Fact]
    public void Receiptless_failure_has_only_the_exact_ordinary_job_finalizer_as_its_production_caller()
    {
        using var application = AssemblyDefinition.ReadAssembly(typeof(LearnStack.Modules.Identity.Application.Invitations.InvitationEmailJobHandlers).Assembly.Location);
        var calls = Calls(application.MainModule.Types.SelectMany(Flatten), "LearnStack.Modules.Identity.Domain.Invitation", "RecordWorkflowFailure");
        calls.Should().ContainSingle();
        calls.Single().DeclaringType.FullName.Should().StartWith("LearnStack.Modules.Identity.Application.Invitations.InvitationEmailJobHandlers/<FinalizeAsync>");
    }

    [Fact]
    public void Provider_function_body_census_rejects_business_relation_or_dynamic_authority_expansion()
    {
        var path = Path.Combine(SourceScan.SourceRoot, "LearnStack.Infrastructure/Persistence/InvitationSmtpStorageDdl.cs");
        var source = File.ReadAllText(path);
        var bodies = Regex.Matches(source, @"CREATE FUNCTION public\.fn_invitation_smtp_.*?AS \$fn\$(.*?)\$fn\$;", RegexOptions.Singleline)
            .Select(match => match.Groups[1].Value).ToArray();
        bodies.Should().HaveCount(2);
        foreach (var body in bodies)
        {
            Violations(body).Should().BeEmpty();
            Violations(body + " SELECT id FROM public.invitations;").Should().Contain("public.invitations");
            Violations(body + " PERFORM set_config('app.tenant_id','x',true);").Should().Contain("authority");
            Violations(body + " EXECUTE statement;").Should().Contain("authority");
        }
    }
    private static string[] Violations(string body)
    {
        var allowed = new[] { "public.background_jobs", "public.background_job_attempts", "public.invitation_email_envelopes", "public.invitation_email_attempt_receipts" };
        var relations = Regex.Matches(body, @"\b(?:FROM|JOIN|INTO|UPDATE)\s+(public\.[a-z_]+)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value).Except(allowed, StringComparer.Ordinal).ToList();
        if (Regex.IsMatch(body, @"\b(?:EXECUTE|set_config)\b", RegexOptions.IgnoreCase)) relations.Add("authority");
        return relations.ToArray();
    }
    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
    private static MethodDefinition[] Calls(IEnumerable<TypeDefinition> types, string type, string name) => types
        .SelectMany(owner => owner.Methods).Where(method => method.HasBody && method.Body.Instructions.Any(instruction =>
            instruction.OpCode is var opcode && (opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj)
            && instruction.Operand is MethodReference called && called.DeclaringType.FullName == type && called.Name == name)).ToArray();
}
