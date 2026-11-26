using FluentAssertions;
using Mono.Cecil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The closure's fence acquirer on the platform connection (ADR-0107 § 7), the census leg beside
/// <c>Identity_Membership_Invariants_Use_Fresh_Ordered_Locks</c>: one class, one method body, in a READ COMMITTED
/// transaction the person's User key and then, within the closure's bounds, every participant tenant's key ascending, each
/// transaction-scoped and each wait bounded by the transaction's own <c>lock_timeout</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a leg of its own.</b> The ordinary acquirer is <c>IIdentityMutationLocks</c>, which fences one tenant inside a
/// tenant-announced application transaction, and the existing census pins its callers. A closure spans every tenant of the
/// person on a connection that announces nothing, so its acquirer is a separate class that census never sees; this one
/// pins that it is the only one, and the order it takes the keys in.
/// </para>
/// <para>
/// <b>What "the only one" means mechanically.</b> In the production IL, the methods that derive both a User key and a
/// Tenant key are the ordinary acquirer and this one; in the production source, the only file that names the platform
/// scope's handle and derives identity lock keys is this one. A second platform acquirer, or a closure owner that took a
/// key itself, fails one of the two. The session-level advisory calls are refused everywhere by the existing census.
/// </para>
/// </remarks>
public sealed class IdentityPlatformFenceConventionTests
{
    private const string AcquirerFile =
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/DataRequests/PlatformIdentityFences.cs";

    private const string LockKeyType = "LearnStack.SharedKernel.Identity.IdentityAdvisoryLockKey";

    [Fact]
    public void The_Closure_Fences_Are_Taken_By_One_Platform_Acquirer_User_First_Then_Each_Tenant_Ascending_For_The_Transaction()
    {
        var users = Callers(LockKeyType, "ForUser");
        var tenants = Callers(LockKeyType, "ForTenant");
        users.Intersect(tenants, StringComparer.Ordinal).Should().BeEquivalentTo(
            [
                "LearnStack.Infrastructure.Persistence.IdentityMutationLocks.AcquireAsync",
                "LearnStack.Modules.Identity.Infrastructure.DataRequests.PlatformIdentityFences.AcquireCoreAsync",
            ],
            "the methods that take a User key and then tenant keys are the application acquirer and the closure's, and no other");

        SourceScan.FilesContaining(SourceScan.SourceRoot, "IPlatformAdminScopeHandle", except: null)
            .Intersect(SourceScan.FilesContaining(SourceScan.SourceRoot, "IdentityAdvisoryLockKey", except: null))
            .Should().Equal([AcquirerFile], "no other holder of the platform scope's handle derives an identity lock key");

        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, AcquirerFile.Replace('/', Path.DirectorySeparatorChar)));
        AcquirerViolations(source).Should().BeEmpty();
    }

    [Theory]
    [InlineData("tenants first", "await handle.ExecuteAsync(TakeExclusive, [new PlatformSqlParameter(\"key\", userKey)], cancellationToken);", "")]
    [InlineData("a shared key", "SELECT pg_catalog.pg_advisory_xact_lock(@key)", "SELECT pg_catalog.pg_advisory_xact_lock_shared(@key)")]
    [InlineData("a session key", "SELECT pg_catalog.pg_advisory_xact_lock(@key)", "SELECT pg_catalog.pg_advisory_lock(@key)")]
    [InlineData("an unbounded wait", "SELECT pg_catalog.set_config('lock_timeout', '3s', true)", "SELECT 1")]
    [InlineData("a session-wide bound", "set_config('lock_timeout', '3s', true)", "set_config('lock_timeout', '3s', false)")]
    [InlineData("descending tenants", ".OrderBy(IdentityAdvisoryLockKey.ForTenant)", ".OrderByDescending(IdentityAdvisoryLockKey.ForTenant)")]
    [InlineData("participants read before the User key", "var participants = await ReadParticipantsAsync(handle, subject, cancellationToken);", "")]
    [InlineData("no isolation asked", "SELECT pg_catalog.current_setting('transaction_isolation')", "SELECT 'read committed'")]
    [InlineData("another isolation admitted", "\"read committed\"", "\"repeatable read\"")]
    [InlineData("isolation asked after the User key", "var isolation = await handle.QueryAsync(ReadCommitted, null, reader => reader.GetString(0), cancellationToken);", "")]
    [InlineData("tenant keys past the bounds", "requestTenant is not { } request || !participants.WithinBounds", "requestTenant is not { } request")]
    public void The_Acquirer_Census_Finds_A_Planted_Departure(string departure, string original, string planted)
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, AcquirerFile.Replace('/', Path.DirectorySeparatorChar)));
        source.Should().Contain(original, departure);
        const string Returned = "        return new PlatformIdentityFenceSet(subject, participants.Groups, tenants, WithinBounds: true);";
        const string Bound = "        await handle.ExecuteAsync(BoundTheWait, null, cancellationToken);";
        const string UserKey = "        var participants = await ReadParticipantsAsync(handle, subject, cancellationToken);";
        var changed = departure switch
        {
            // Move the User key below the tenant loop, the participant read above the User key, or the isolation question
            // below the User key.
            "tenants first" => source.Replace(original, planted, StringComparison.Ordinal).Replace(
                Returned, "        " + original + "\n" + Returned, StringComparison.Ordinal),
            "participants read before the User key" => source.Replace(original, planted, StringComparison.Ordinal).Replace(
                Bound, "        " + original + "\n" + Bound, StringComparison.Ordinal),
            "isolation asked after the User key" => source.Replace(original, planted, StringComparison.Ordinal).Replace(
                UserKey, "        " + original + "\n" + UserKey, StringComparison.Ordinal),
            _ => source.Replace(original, planted, StringComparison.Ordinal),
        };

        changed.Should().NotBe(source, departure);
        AcquirerViolations(changed).Should().NotBeEmpty(departure);
    }

    /// <summary>
    /// The closure's Finalize takes its fences only through the acquirer, once in each of its two transactions: the User key
    /// alone before its redaction and intent checkpoint, which changes no User and no request and so holds no participant
    /// tenant's key while it redacts, and every key after the provider call and before the User moves to Closed, because the
    /// first transaction's key ended at the checkpoint's commit. The database refuses the second transaction's changes
    /// without the keys; this pins the source that takes them.
    /// </summary>
    [Fact]
    public void The_Closure_Finalize_Takes_Its_Fences_Through_The_Acquirer_Once_In_Each_Transaction()
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, FinalizeFile.Replace('/', Path.DirectorySeparatorChar)));
        FinalizeViolations(source).Should().BeEmpty();
    }

    [Theory]
    [InlineData("no second acquisition", "var again = await PlatformIdentityFences.AcquireAsync(handle, request.Subject, request.Tenant, cancellationToken);",
        "var again = fences;")]
    [InlineData("the first acquisition after the checkpoint", "var fences = await PlatformIdentityFences.AcquireUserAsync(handle, request.Subject, cancellationToken);",
        "var fences = (PlatformIdentityFenceSet)null!;")]
    [InlineData("the first acquisition fences every tenant", "var fences = await PlatformIdentityFences.AcquireUserAsync(handle, request.Subject, cancellationToken);",
        "var fences = await PlatformIdentityFences.AcquireAsync(handle, request.Subject, request.Tenant, cancellationToken);")]
    [InlineData("the second acquisition fences the User alone", "var again = await PlatformIdentityFences.AcquireAsync(handle, request.Subject, request.Tenant, cancellationToken);",
        "var again = await PlatformIdentityFences.AcquireUserAsync(handle, request.Subject, cancellationToken);")]
    [InlineData("a key taken by hand", "var again = await PlatformIdentityFences.AcquireAsync(handle, request.Subject, request.Tenant, cancellationToken);",
        "await handle.ExecuteAsync(\"SELECT pg_catalog.pg_advisory_xact_lock(@key)\", null, cancellationToken); var again = await PlatformIdentityFences.AcquireAsync(handle, request.Subject, request.Tenant, cancellationToken);")]
    public void The_Finalize_Census_Finds_A_Planted_Departure(string departure, string original, string planted)
    {
        var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, FinalizeFile.Replace('/', Path.DirectorySeparatorChar)));
        source.Should().Contain(original, departure);
        var changed = source.Replace(original, planted, StringComparison.Ordinal);

        changed.Should().NotBe(source, departure);
        FinalizeViolations(changed).Should().NotBeEmpty(departure);
    }

    private const string FinalizeFile =
        "Modules/Identity/LearnStack.Modules.Identity.Infrastructure/DataRequests/AccountClosureOperatorOperations.Finalize.cs";

    /// <summary>
    /// What Finalize's code must say, read without comments and whitespace: exactly two acquisitions, the User key alone
    /// before the redaction and the checkpoint, every key after the provider call and before the account moves, and no
    /// advisory key of its own.
    /// </summary>
    private static List<string> FinalizeViolations(string source)
    {
        var code = SourceText.WithoutWhitespace(SourceText.WithoutComments(source));
        var violations = new List<string>();
        const string AcquireUser = "awaitPlatformIdentityFences.AcquireUserAsync(handle,request.Subject,cancellationToken);";
        const string Acquire = "awaitPlatformIdentityFences.AcquireAsync(handle,request.Subject,request.Tenant,cancellationToken);";
        if (CountOf(code, AcquireUser) != 1 || CountOf(code, Acquire) != 1 || CountOf(code, "PlatformIdentityFences.Acquire") != 2)
            violations.Add("Finalize takes the User fence alone once and every fence once, through the acquirer, once a transaction");
        if (System.Text.RegularExpressions.Regex.IsMatch(code, "advisory|IdentityAdvisoryLockKey", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            violations.Add("Finalize takes no advisory key and derives no lock key itself");

        string[] order =
        [
            AcquireUser,
            "awaitRedactAsync(",
            "awaithandle.WriteIntentAsync(",
            "awaitRemoveAccountAsync(providerSubject,cancellationToken);",
            Acquire,
            "awaithandle.ExecuteAsync(CloseAccount,",
        ];
        var position = -1;
        foreach (var step in order)
        {
            position = code.IndexOf(step, position + 1, StringComparison.Ordinal);
            if (position < 0)
            {
                violations.Add("the fences, the redaction, the checkpoint, the provider, the fences again, then the account's move");
                break;
            }
        }

        return violations;
    }

    /// <summary>
    /// What the acquirer's code must say, read without comments and whitespace: the transaction's isolation asked first and
    /// only READ COMMITTED admitted, a transaction-scoped bound, the exclusive transaction-scoped key only, and in order the
    /// isolation, the bound, the User key, the participant read, the refusal of the tenant keys past the closure's bounds,
    /// the ascending tenant order and the tenant keys.
    /// </summary>
    private static List<string> AcquirerViolations(string source)
    {
        var code = SourceText.WithoutWhitespace(SourceText.WithoutComments(source));
        var violations = new List<string>();
        string Squeeze(string text) => SourceText.WithoutWhitespace(text);

        if (CountOf(code, Squeeze("\"SELECT pg_catalog.current_setting('transaction_isolation')\"")) != 1
            || CountOf(code, Squeeze("string.Equals(isolation[0], \"read committed\", StringComparison.Ordinal)")) != 1)
            violations.Add("the transaction's isolation is asked once, and only READ COMMITTED is admitted");
        if (CountOf(code, Squeeze("\"SELECT pg_catalog.set_config('lock_timeout', '3s', true)\"")) != 1)
            violations.Add("the wait is bounded once, by a transaction-local lock_timeout");
        if (CountOf(code, Squeeze("\"SELECT pg_catalog.pg_advisory_xact_lock(@key)\"")) != 1
            || code.Contains("advisory_xact_lock_shared", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(code, @"pg_(?:try_)?advisory_(?:lock|unlock)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            violations.Add("every key is exclusive and transaction-scoped");

        string[] order =
        [
            "varisolation=awaithandle.QueryAsync(ReadCommitted,null,reader=>reader.GetString(0),cancellationToken);",
            "awaithandle.ExecuteAsync(BoundTheWait,null,cancellationToken);",
            "awaithandle.ExecuteAsync(TakeExclusive,[newPlatformSqlParameter(\"key\",userKey)],cancellationToken);",
            "varparticipants=awaitReadParticipantsAsync(handle,subject,cancellationToken);",
            "if(requestTenantisnot{}request||!participants.WithinBounds)",
            ".OrderBy(IdentityAdvisoryLockKey.ForTenant)",
            "foreach(vartenantintenants)",
            "newPlatformSqlParameter(\"key\",IdentityAdvisoryLockKey.ForTenant(tenant))",
        ];
        var positions = order.Select(step => code.IndexOf(step, StringComparison.Ordinal)).ToArray();
        if (positions.Any(position => position < 0) || positions.Zip(positions.Skip(1)).Any(pair => pair.First >= pair.Second))
            violations.Add("the isolation, the bound, then the User key, then the participants under it, the bounds, then each tenant key ascending");
        if (CountOf(code, "IdentityAdvisoryLockKey.ForUser(") != 1)
            violations.Add("one User key, the person's");
        return violations;
    }

    private static int CountOf(string code, string needle)
    {
        var count = 0;
        for (var index = code.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = code.IndexOf(needle, index + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string[] Callers(string typeName, string methodName) => ProductionAssemblies.All().SelectMany(assembly =>
    {
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        return module.Types.Where(type => type.Name != "<Module>")
            .SelectMany(type => Il.Methods(type).Where(method => method.Definition.HasBody
                    && method.Definition.Body.Instructions.Any(instruction => instruction.Operand is MethodReference call
                        && call.DeclaringType.FullName == typeName && call.Name == methodName))
                .Select(method => type.FullName + "." + method.DeclaredAs.TrimStart('.'))).ToArray();
    }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
