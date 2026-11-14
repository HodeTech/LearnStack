using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Federation;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0099 Amendment 6 (H): the native self read announces the host tenant through one enumerated, read-only, host-only
/// setter whose only read is the host tenant's Active, non-tombstone federation alias. Database Standards enumerate it in
/// the logical-setter census; <c>Out_Of_Band_Setters_Open_Read_Only_Transactions</c> separately proves its
/// <c>SET TRANSACTION READ ONLY</c> precedes the announcement.
/// </summary>
public sealed class FederationHostAliasSetterConventionTests
{
    private const string ReaderType = "LearnStack.Modules.Identity.Infrastructure.Persistence.FederationHostAliasReader";
    private const string Port = "LearnStack.Modules.Identity.Application.Contracts.Entry.IFederationHostAliasReader";
    private const string Entry = "LearnStack.Api.Authentication.GlobalSelfEntryMiddleware";

    private const string ReadOnlyStatement = "SET TRANSACTION READ ONLY";
    private const string Announcement = "SELECT set_config('app.tenant_id', @tenant, true)";

    /// <summary>The one read, as it must be written: the registry only, the host tenant, live and Active.</summary>
    private static readonly string AliasStatement = Normalize(
        "SELECT alias,protocol FROM public.federation_connections WHERE tenant_id=@tenant AND "
        + FederationConnectionReads.LiveSql + " AND state='Active'");

    [Fact]
    public void Federation_Host_Alias_Setter_Is_Read_Only_And_Host_Only()
    {
        using var module = ModuleDefinition.ReadModule(Path.Combine(AppContext.BaseDirectory,
            "LearnStack.Modules.Identity.Infrastructure.dll"));
        var reader = module.GetType(ReaderType) ?? throw new InvalidOperationException(ReaderType + " is the enumerated setter.");
        StatementOffenders(Strings(reader)).Should().BeEmpty(
            "Fix: the host alias setter runs exactly SET TRANSACTION READ ONLY, the app.tenant_id announcement and the one "
            + "Active alias read of federation_connections; it announces no other GUC and writes nothing");

        var implementations = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.GetInterfaces().Any(contract => contract.FullName == Port))
            .Select(type => type.FullName!).Order(StringComparer.Ordinal).ToArray();
        implementations.Should().Equal([ReaderType], "the setter is enumerated: one implementation of the port");

        var callers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var assembly in ProductionAssemblies.All())
        {
            using var production = ModuleDefinition.ReadModule(assembly.Location);
            foreach (var type in production.Types.SelectMany(Flatten))
                foreach (var method in type.Methods.Where(method => method.HasBody))
                    if (method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference target
                            && target.DeclaringType.FullName == Port && target.Name == "ReadActiveAliasAsync"))
                        callers.Add(Owner(type));
        }
        callers.Should().Equal([Entry], "only the global self read consults the host alias setter");
    }

    [Fact]
    public void The_host_alias_setter_detector_refuses_a_write_a_widened_announcement_and_a_second_read()
    {
        string[] clean = [ReadOnlyStatement, Announcement, AliasStatement, "tenant", "The host federation alias is unavailable."];
        StatementOffenders(clean).Should().BeEmpty();
        foreach (var planted in new[]
        {
            "UPDATE public.federation_connections SET state='Draft' WHERE tenant_id=@tenant",
            "INSERT INTO public.audit_log (id) VALUES (@id)",
            "DELETE FROM public.federation_connections WHERE tenant_id=@tenant",
            "SELECT set_config('app.organization_id', @organization, true)",
            "SELECT set_config('app.scope', 'tenant', true)",
            "SELECT feature_key FROM public.platform_entitlement_cache WHERE tenant_id=@tenant",
            "SELECT alias FROM public.federation_connections WHERE tenant_id=@tenant",
            "set local app.tenant_id = @tenant",
        })
            StatementOffenders([.. clean, planted]).Should().NotBeEmpty("a planted statement is refused: " + planted);
        StatementOffenders(clean.Where(text => text != ReadOnlyStatement)).Should().NotBeEmpty("the read-only statement is required");
        StatementOffenders(clean.Where(text => text != AliasStatement)).Should().NotBeEmpty("the one read is required");
        StatementOffenders([.. clean, AliasStatement]).Should().NotBeEmpty("the one read is issued once");
        StatementOffenders(clean.Select(text => text == AliasStatement ? text.Replace(" AND state='Active'", "", StringComparison.Ordinal) : text))
            .Should().NotBeEmpty("an Active-less read is a different statement");
    }

    private static readonly Regex Sql = new(
        @"^\s*(?:SELECT|SET|INSERT|UPDATE|DELETE|MERGE|TRUNCATE|CALL|COPY|LOCK|WITH|DO|CREATE|ALTER|DROP|GRANT|REVOKE|RESET|DISCARD)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static List<string> StatementOffenders(IEnumerable<string> strings)
    {
        var statements = strings.Where(text => Sql.IsMatch(text) || text.Contains("set_config", StringComparison.OrdinalIgnoreCase))
            .Select(Normalize).ToList();
        var offenders = statements.Where(text => text != ReadOnlyStatement && text != Announcement && text != AliasStatement)
            .Select(text => "unexpected statement: " + text).ToList();
        foreach (var required in new[] { ReadOnlyStatement, Announcement, AliasStatement })
            if (statements.Count(text => text == required) != 1) offenders.Add("required exactly once: " + required);
        return offenders;
    }

    private static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private static IEnumerable<string> Strings(TypeDefinition type) => Flatten(type)
        .SelectMany(nested => nested.Methods).Where(method => method.HasBody)
        .SelectMany(method => method.Body.Instructions).Where(instruction => instruction.OpCode == OpCodes.Ldstr)
        .Select(instruction => (string)instruction.Operand);

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));

    /// <summary>A compiler-generated state machine or closure is attributed to its declaring type.</summary>
    private static string Owner(TypeDefinition type)
    {
        while (type.DeclaringType is { } outer && type.Name.Contains('<', StringComparison.Ordinal)) type = outer;
        return type.FullName;
    }
}
