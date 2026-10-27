using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using EvidenceCensus = LearnStack.Tests.Architecture.TrustedOperationSourceConventionTests.Census;

namespace LearnStack.Tests.Architecture;

public sealed partial class IdentityAuditConventionTests
{
    private const string DatabaseProof = "LearnStack.Infrastructure.Persistence.DatabaseQualificationProof";
    private const string MainQualification = "LearnStack.Modules.Identity.Infrastructure.Bindings.IdentityAuthorityQualification";
    private const string SourcesStartup = "LearnStack.Api.Authentication.TenantAuthorizationSourcesStartupService";
    private const string AuthorizationSource = "LearnStack.Infrastructure.Persistence.IdentityAuthorizationReadSource";
    private const string AuthorizationOwner = "LearnStack.Infrastructure.Persistence.AuthorizationReadOwner";

    private static void CheckDatabaseProofControls(EvidenceCensus census)
    {
        DatabaseProofOffenders(census).Should().BeEmpty();
        foreach (var name in new[] { "OpenAsync", "Bind" })
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Ldftn })
                EvidenceDirty(census, DatabaseProofOffenders, () => EvidenceExtraCall(census, census.Method(DatabaseProof, name), opcode));
        foreach (var (owner, method, target, gate) in new[]
        {
            (DatabaseProof, "OpenAsync", EvidenceRoleGuard, "RefuseBypassRoleAsync"),
            (DatabaseProof, "Bind", DatabaseProof, "RequireActive"),
            (MainQualification, "OpenDatabaseQualificationProofAsync", DatabaseProof, "OpenAsync"),
            (AuthorizationOwner, "QualifyAsync", DatabaseProof, "Bind"),
            (EvidenceSession, "QualifyAsync", DatabaseProof, "Bind"),
        }) EvidenceDirty(census, DatabaseProofOffenders, () => EvidenceRemoveCall(census.Body(owner, method), target, gate));
        foreach (var caller in census.Methods.Where(method =>
                     census.Owner(method) == SourcesStartup + "::StartAsync"
                     || method.Module.Assembly.Name.Name == "LearnStack.Tools.Seeder" && census.Owner(method) == "Program::<Main>$"))
            foreach (var instruction in caller.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                         && target.Name == "QualifyAsync" && target.Parameters.Any(parameter => parameter.ParameterType.FullName == DatabaseProof)).ToArray())
            {
                var bound = (MethodReference)instruction.Operand;
                var legacy = census.Type(bound.DeclaringType.FullName).Methods.Single(method => method.Name == "QualifyAsync" && method.Parameters.Count == 3);
                EvidenceDirty(census, DatabaseProofOffenders, () => EvidenceReplace(instruction, instruction.OpCode, legacy));
            }
    }

    private static List<string> DatabaseProofOffenders(EvidenceCensus census)
    {
        var errors = new List<string>();
        EvidenceCallers(census, errors, DatabaseProof, "OpenAsync", [(MainQualification, "OpenDatabaseQualificationProofAsync", 1)]);
        EvidenceCallers(census, errors, DatabaseProof, "Bind", [(AuthorizationOwner, "QualifyAsync", 1), (EvidenceSession, "QualifyAsync", 1)]);
        EvidenceCallers(census, errors, DatabaseProof, ".ctor", [(DatabaseProof, "OpenAsync", 1)]);
        EvidenceRequire(census, errors, DatabaseProof, "OpenAsync", EvidenceRoleGuard, "RefuseBypassRoleAsync");
        EvidenceRequire(census, errors, DatabaseProof, "Bind", DatabaseProof, "RequireActive");
        foreach (var owner in new[] { AuthorizationOwner, EvidenceSession })
            EvidenceRequire(census, errors, owner, "QualifyAsync", DatabaseProof, "Bind");
        var openCallers = census.Methods.Where(method => method.Body.Instructions.Any(instruction =>
            EvidenceIsCall(instruction, MainQualification, "OpenDatabaseQualificationProofAsync"))).ToArray();
        if (openCallers.Length != 2 || !openCallers.Any(method => census.Owner(method) == SourcesStartup + "::StartAsync")
            || !openCallers.Any(method => method.Module.Assembly.Name.Name == "LearnStack.Tools.Seeder" && census.Owner(method) == "Program::<Main>$"))
            errors.Add("Only the actual API/Seeder startup roots open the business database witness.");
        var startup = census.Body(SourcesStartup, "StartAsync");
        foreach (var target in new[] { AuthorizationSource, EvidenceSource }) RequireBound(startup, target, 1);
        var seeder = census.Methods.Single(method => method.Module.Assembly.Name.Name == "LearnStack.Tools.Seeder"
            && census.Owner(method) == "Program::<Main>$" && EvidenceCalls(method, EvidenceSource, "QualifyAsync").Any());
        RequireBound(seeder, EvidenceSource, 1);
        var proof = census.Type(DatabaseProof);
        if (!proof.IsSealed || proof.Methods.Where(method => method.IsConstructor).Any(method => !method.IsPrivate)
            || proof.Fields.Where(field => field.FieldType.FullName is "Npgsql.NpgsqlConnection" or "Npgsql.NpgsqlTransaction")
                .Any(field => !field.IsPrivate || !field.IsInitOnly)
            || proof.Methods.Where(method => method.IsPublic).Any(method => EvidenceRaw(method.ReturnType)))
            errors.Add("The startup witness exposes no raw connection/transaction or construction authority.");
        return errors;

        void RequireBound(MethodDefinition method, string targetType, int count)
        {
            var calls = EvidenceCalls(method, targetType, "QualifyAsync").ToArray();
            if (calls.Length != count || calls.Any(call => call.Operand is not MethodReference target
                || target.Parameters.Count != 4 || target.Parameters[2].ParameterType.FullName != DatabaseProof))
                errors.Add("Startup qualification must consume the actual business witness: " + targetType);
        }
    }
}
