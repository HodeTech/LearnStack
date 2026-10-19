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
                     nameof(AuthorityProbe.RolePolicy), nameof(AuthorityProbe.ClaimPolicy), nameof(AuthorityProbe.InterfaceRoleMetadata),
                     nameof(AuthorityProbe.RawTokenClaim), nameof(AuthorityProbe.RawTokenSubject),
                     nameof(AuthorityProbe.LegacyTokenPayload), nameof(AuthorityProbe.RawHeaderReparse) })
        {
            var dirty = methods.Single(method => method.Name == name);
            Offenders(modules, [dirty]).Should().Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal), name);
            Action blinded = () => Offenders(modules, [dirty], _ => false).Should()
                .Contain(value => value.StartsWith("Raw claim/role authority:", StringComparison.Ordinal));
            blinded.Should().Throw<Xunit.Sdk.XunitException>("a weakened raw-reader predicate must fail its dirty control: " + name);
        }
        foreach (var name in new[] { nameof(AuthorityProbe.Forge), nameof(AuthorityProbe.ForgeFeature),
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
            && instruction.Operand is MethodReference target && Is(target.DeclaringType, Identity));
        var original = construction.Operand;
        construction.Operand = api.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)!);
        Offenders(modules).Should().NotBeEmpty("the actual factory call is a required nonempty subject");
        construction.Operand = original;

        identity.IsSealed = false;
        Offenders(modules).Should().NotBeEmpty("the projection cannot gain a mutable/subclass authority channel");
        identity.IsSealed = true;
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
            && Is(target.DeclaringType, Feature) && target.Name == "set_Identity");
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
        Offenders(modules).Should().BeEmpty("all mutations of actual production subjects are restored");
    });

    private static class AuthorityProbe
    {
        internal static bool Role(ClaimsPrincipal principal) => principal.IsInRole("tenant-admin");
        internal static bool InterfaceRole(System.Security.Principal.IPrincipal principal) => principal.IsInRole("tenant-admin");
        internal static string? IdentityName(System.Security.Principal.IIdentity identity) => identity.Name;
        internal static Claim? Claim(ClaimsPrincipal principal) => principal.FindFirst("permission");
        internal static bool ClaimSet(ClaimsPrincipal principal) => principal.HasClaim("scope", "write");
        internal static string Email(Claim claim) => claim.Value;
        internal static bool Authenticated(System.Security.Principal.IIdentity identity) => identity.IsAuthenticated;
        internal static ClaimsPrincipal RawUser(HttpContext context) => context.User;
        internal static AuthorizationPolicyBuilder RolePolicy(AuthorizationPolicyBuilder builder) => builder.RequireRole("tenant-admin");
        internal static AuthorizationPolicyBuilder ClaimPolicy(AuthorizationPolicyBuilder builder) => builder.RequireClaim("permission", "write");
        internal static IAuthorizeData InterfaceRoleMetadata(IAuthorizeData metadata)
        {
            metadata.Roles = "tenant-admin";
            return metadata;
        }
        internal static bool RawTokenClaim(Microsoft.IdentityModel.JsonWebTokens.JsonWebToken token) => token.GetPayloadValue<string>("permission") == "write";
        internal static bool RawTokenSubject(Microsoft.IdentityModel.JsonWebTokens.JsonWebToken token) => token.Subject == "tenant-admin";
        internal static bool LegacyTokenPayload(System.IdentityModel.Tokens.Jwt.JwtSecurityToken token) => (string)token.Payload["permission"] == "write";
        internal static bool RawHeaderReparse(HttpContext context) => new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(
            context.Request.Headers.Authorization.ToString()["Bearer ".Length..]).GetPayloadValue<string>("permission") == "write";
        internal static ValidatedTenantIdentity Forge() => new("issuer", "subject", UserId.From(Guid.Parse("a2999254-8355-43da-86ea-5cbebf105d29")), null, null);
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
