using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Reproduce the immutable historical console graph without changing the observation or expectation.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenantRealmStockBaselineTests(ITestOutputHelper output)
{
    private const string ExportSha256 = "39659a4e505c8ad3c1b2963e69e58dadb725e4fb37b408dcf87214a2bb0d2d07";
    private const string RedactedExportSha256 = "ba9a6d6783d8849387e1a194f650a853f9596ade032a5b48eb3d19695ade7b1d";
    private const string CanonicalSha256 = "fe38c56fdd99f2073a901e4b7261ca9f0f4d9f24c45e1effea1e69261781a476";
    private static readonly string[] DefaultScopes = ["acr", "basic", "email", "profile", "roles", "web-origins"];
    private static readonly string[] OptionalScopes = ["address", "microprofile-jwt", "offline_access", "organization", "phone"];
    private static readonly JsonSerializerOptions ReceiptOptions = new() { WriteIndented = true };

    [Fact]
    public async Task Historical_24705a7a_bootstrap_matches_the_fixed_26_6_2_scope_role_and_mapper_baseline()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(KeycloakFixture.RealmPath("learnstack"))!, "..", "..", ".."));
        var historical = await HistoricalImportAsync(root);
        var directory = Directory.CreateTempSubdirectory("learnstack-stock-proof-").FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        KeycloakFixture? fixture = null;
        StockProviderResources? resources = null;
        var watch = Stopwatch.StartNew();
        try
        {
            fixture = await KeycloakFixture.StartAsync(historical);
            resources = await fixture.StockProviderResourcesAsync();
            (await fixture.StockProviderVersionAsync()).Should().Be("26.6.2");
            using var admin = await fixture.CreateAdminClientAsync();
            var graph = await CaptureAsync(admin);
            var capture = JsonSerializer.SerializeToUtf8Bytes(graph);
            var capturePath = Path.Combine(directory, "private-stock-provider-graph.json");
            await WritePrivateAsync(capturePath, capture);
            await AssertExtractorAsync(root, capturePath);
            var receipt = new
            {
                historicalCommit = "24705a7a",
                historicalExportSha256 = ExportSha256,
                redactedHistoricalExportSha256 = RedactedExportSha256,
                privateImportSha256 = Hash(System.Text.Encoding.UTF8.GetBytes(historical)),
                credentialTransformation = "Only the two historical user password values and learnstack-api secret are redacted in the committed fixture; private test values replace those placeholders in memory before import.",
                providerImage = KeycloakFixture.Image,
                authenticatedProviderVersion = "26.6.2",
                resources.ImageId,
                resources.RepoDigests,
                resources.ProviderContainerId,
                resources.DatabaseContainerId,
                resources.NetworkId,
                resources.OperatingSystem,
                resources.Architecture,
                resources.ImageDescriptorDigest,
                resources.ImageDescriptorMediaType,
                resources.PlatformManifestDigest,
                captureSha256 = Hash(capture),
                canonicalSha256 = CanonicalSha256,
                extractorSha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(root, "scripts", "extract-development-realm-stock-console.mjs"))),
                classifierSha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(root, "scripts", "development-realm-stock-console.mjs"))),
                endpointCount = graph.Count,
                stockScopeCount = DefaultScopes.Length + OptionalScopes.Length,
                referencedRoleCount = 4,
                elapsedSeconds = watch.Elapsed.TotalSeconds,
                limitations = "Read-only historical bootstrap qualification only; no automatic preparation, detachment, token or profile-write proof.",
            };
            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, ReceiptOptions);
            await WritePrivateAsync(Path.Combine(directory, "receipt.json"), receiptBytes);
            // Explicit opt-in retains private DTOs in a caller-owned private temporary directory.
            // The ordinary test leaves no raw provider response on disk.
            var retain = Environment.GetEnvironmentVariable("LEARNSTACK_STOCK_PROOF_PRIVATE_OUTPUT");
            if (retain is not null)
            {
                if (!Path.IsPathFullyQualified(retain) || !Directory.Exists(retain))
                    throw new InvalidOperationException("Stock proof private output directory is invalid.");
                if (!OperatingSystem.IsWindows()
                    && (File.GetUnixFileMode(retain) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    throw new InvalidOperationException("Stock proof private output directory is not private.");
                await WritePrivateAsync(Path.Combine(retain, "private-stock-provider-graph.json"), capture);
                await WritePrivateAsync(Path.Combine(retain, "receipt.json"), receiptBytes);
            }
            output.WriteLine("Stock historical baseline matched: {0}; endpoints={1}; scopes=11; referenced roles=4.", CanonicalSha256, graph.Count);
            output.WriteLine("Owned provider image: {0}; digests: {1}.", resources.ImageId, string.Join(",", resources.RepoDigests));
            output.WriteLine("Historical source: {0}; redacted fixture: {1}; capture: {2}.", ExportSha256, RedactedExportSha256, Hash(capture));
        }
        finally
        {
            try
            {
                if (fixture is not null) await fixture.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (resources is not null)
                    {
                        await KeycloakFixture.AssertStockResourcesAbsentAsync(resources);
                        output.WriteLine("Joined cleanup absent: containers={0},{1}; network={2}.",
                            resources.ProviderContainerId, resources.DatabaseContainerId, resources.NetworkId);
                    }
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
        }
    }

    [Fact]
    public async Task Historical_stock_ordinary_reconciliation_detaches_only_three_console_edges_and_then_writes_zero()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(KeycloakFixture.RealmPath("learnstack"))!, "..", "..", ".."));
        var historical = await HistoricalImportAsync(root);
        await using var fixture = await KeycloakFixture.StartAsync(historical);
        var resources = await fixture.StockProviderResourcesAsync();
        var watch = Stopwatch.StartNew();
        try
        {
            (await fixture.StockProviderVersionAsync()).Should().Be("26.6.2");
            using var admin = await fixture.CreateAdminClientAsync();
            var before = await CaptureAsync(admin);
            var assignmentsBefore = await AssignmentsAsync(admin, before["clients"]);
            var allUserIdsBefore = (await admin.GetAsync("users?max=100&briefRepresentation=false"))
                .EnumerateArray().Select(Id).ToArray();
            var account = Named(before["clients"], "clientId", "account");
            var console = Named(before["clients"], "clientId", "account-console");
            var accountPath = "clients/" + Id(account);
            var consolePath = "clients/" + Id(console);
            var userIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
            using var original = JsonDocument.Parse(historical);
            foreach (var imported in original.RootElement.GetProperty("users").EnumerateArray())
            {
                var username = Text(imported, "username");
                var user = await admin.UserAsync(username);
                var id = Id(user);
                userIds.Add(username, id);
                var credential = await admin.GetAsync("users/" + id + "/credentials");
                credential.EnumerateArray().Any(value => Text(value, "type") == "password").Should().BeTrue(
                    "the preservation control starts with a real imported password credential");
                credentials.Add(username, Hash(JsonSerializer.SerializeToUtf8Bytes(credential)));
            }
            var api = Named(before["clients"], "clientId", "learnstack-api");
            var apiSecret = await admin.GetAsync("clients/" + Id(api) + "/client-secret");

            // This is test-owned setup for Step 1's ordinary path. It is deliberately
            // separate from the unimplemented production preparation protocol.
            var profile = JsonNode.Parse(before["users/profile"].GetRawText())!.AsObject();
            var definitions = profile["attributes"]!.AsArray();
            var desired = JsonNode.Parse(await File.ReadAllTextAsync(KeycloakFixture.RealmPath("learnstack")))!;
            var declared = JsonNode.Parse(desired["components"]!["org.keycloak.userprofile.UserProfileProvider"]![0]!
                ["config"]!["kc.user.profile.config"]![0]!.GetValue<string>())!["attributes"]!.AsArray();
            foreach (var name in new[] { "user_id", "tenant_id", "organization_id" })
            {
                definitions.Any(value => value!["name"]!.GetValue<string>() == name).Should().BeFalse();
                definitions.Add(declared.Single(value => value!["name"]!.GetValue<string>() == name)!.DeepClone());
            }
            (await admin.PutAsync("users/profile", profile)).Should().Be(HttpStatusCode.OK);
            JsonNode.DeepEquals(profile, JsonNode.Parse((await admin.GetAsync("users/profile")).GetRawText())).Should().BeTrue(
                "an independent GET must prove the entire test-owned metadata append, including ordered arrays");

            var first = await KeycloakReconciliationDriver.RunAsync(fixture);
            first.Success.Should().BeTrue("closed ordinary reconciliation outcome: {0}/{1} after {2} writes",
                first.Stage, first.Reason, first.Mutations);
            first.Mutations.Should().Be(16, "thirteen ordinary approved mutations plus exactly three console assignment DELETEs");
            var after = await CaptureAsync(admin, historicalAssignments: false);
            Names(after[consolePath + "/default-client-scopes"]).Should().Equal(DefaultScopes.Where(value => value != "roles"));
            Names(after[consolePath + "/optional-client-scopes"]).Should().Equal(OptionalScopes.Where(value => value is not ("microprofile-jwt" or "offline_access")));
            Same(before[accountPath], after[accountPath]).Should().BeTrue("the account client is unchanged");
            Same(before[accountPath + "/roles"], after[accountPath + "/roles"]).Should().BeTrue("all account role definitions and composites are retained");
            foreach (var scopeName in DefaultScopes.Concat(OptionalScopes))
            {
                var scope = Named(before["client-scopes"], "name", scopeName);
                var path = "client-scopes/" + Id(scope);
                foreach (var suffix in new[] { "", "/protocol-mappers/models", "/scope-mappings" })
                    Same(before[path + suffix], after[path + suffix]).Should().BeTrue(
                        "every stock global scope definition, effective mapper and allowance is unchanged");
            }
            var assignmentsAfter = await AssignmentsAsync(admin, after["clients"]);
            foreach (var (path, prior) in assignmentsBefore)
            {
                var expected = prior.EnumerateArray().Where(value => path == consolePath + "/default-client-scopes"
                    ? Text(value, "name") != "roles"
                    : path != consolePath + "/optional-client-scopes" || Text(value, "name") is not ("microprofile-jwt" or "offline_access"))
                    .ToList();
                var owner = before["clients"].EnumerateArray().Single(client => path.StartsWith("clients/" + Id(client) + "/", StringComparison.Ordinal));
                if (Text(owner, "clientId") is "learnstack-web" or "learnstack-api")
                {
                    var declaredClient = desired["clients"]!.AsArray().Single(client => client!["clientId"]!.GetValue<string>() == Text(owner, "clientId"))!;
                    var category = path.EndsWith("/default-client-scopes", StringComparison.Ordinal) ? "defaultClientScopes" : "optionalClientScopes";
                    foreach (var declaredName in declaredClient[category]!.AsArray().Select(value => value!.GetValue<string>()))
                    {
                        if (expected.Any(value => Text(value, "name") == declaredName)) continue;
                        var scope = Named(after["client-scopes"], "name", declaredName);
                        expected.Add(JsonSerializer.SerializeToElement(new { id = Text(scope, "id"), name = declaredName }));
                    }
                }
                SameSet(expected, assignmentsAfter[path]).Should().BeTrue(
                    "the exact edge delta is three console removals plus declared web/API scope additions; every other edge is preserved");
            }
            before["clients"].EnumerateArray().Select(Id).Order(StringComparer.Ordinal).Should().Equal(
                after["clients"].EnumerateArray().Select(Id).Order(StringComparer.Ordinal), "no client is deleted or recreated");
            var oldScopeIds = before["client-scopes"].EnumerateArray().Select(Id).ToHashSet(StringComparer.Ordinal);
            var newScopes = after["client-scopes"].EnumerateArray().Where(scope => !oldScopeIds.Contains(Id(scope))).ToArray();
            oldScopeIds.Except(after["client-scopes"].EnumerateArray().Select(Id), StringComparer.Ordinal).Should().BeEmpty(
                "no global client scope is deleted");
            var approvedMissingScopes = desired["clientScopes"]!.AsArray().Select(scope => scope!["name"]!.GetValue<string>())
                .Except(Names(before["client-scopes"]), StringComparer.Ordinal).Order(StringComparer.Ordinal);
            newScopes.Select(scope => Text(scope, "name")).Order(StringComparer.Ordinal).Should().Equal(approvedMissingScopes,
                "only missing scopes from the approved declaration are created");
            Same(before["roles"], after["roles"]).Should().BeTrue("realm roles are retained");
            Same(before["groups"], after["groups"]).Should().BeTrue("groups are retained");
            foreach (var (path, role) in before.Where(value => value.Key.StartsWith("roles-by-id/", StringComparison.Ordinal)))
                Same(role, after[path]).Should().BeTrue("all existing role DTOs and composite edges are retained");
            var allUserIdsAfter = (await admin.GetAsync("users?max=100&briefRepresentation=false"))
                .EnumerateArray().Select(Id).ToArray();
            allUserIdsBefore.Except(allUserIdsAfter, StringComparer.Ordinal).Should().BeEmpty("no preexisting user is deleted");
            foreach (var (username, id) in userIds)
            {
                (Id(await admin.UserAsync(username)) == id).Should().BeTrue("original accounts retain their provider IDs");
                (Hash(JsonSerializer.SerializeToUtf8Bytes(await admin.GetAsync("users/" + id + "/credentials"))) == credentials[username])
                    .Should().BeTrue("original credential DTO hashes remain unchanged");
            }
            Same(apiSecret, await admin.GetAsync("clients/" + Id(api) + "/client-secret")).Should().BeTrue(
                "the imported private API secret is retained");
            var rerun = await KeycloakReconciliationDriver.RunAsync(fixture);
            rerun.Success.Should().BeTrue("closed rerun outcome: {0}/{1}", rerun.Stage, rerun.Reason);
            rerun.Mutations.Should().Be(0);
            output.WriteLine("Historical ordinary stock path: 16 mutations, then zero; retained five default/three optional edges; elapsed={0:F3}s.", watch.Elapsed.TotalSeconds);
        }
        finally
        {
            await fixture.DisposeAsync();
            await KeycloakFixture.AssertStockResourcesAbsentAsync(resources);
            output.WriteLine("Joined cleanup absent: containers={0},{1}; network={2}.",
                resources.ProviderContainerId, resources.DatabaseContainerId, resources.NetworkId);
        }
    }

    private static async Task<Dictionary<string, JsonElement>> AssignmentsAsync(KeycloakAdminClient admin, JsonElement clients)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var client in clients.EnumerateArray())
            foreach (var category in new[] { "default", "optional" })
            {
                var path = "clients/" + Id(client) + "/" + category + "-client-scopes";
                result.Add(path, await admin.GetAsync(path));
            }
        return result;
    }

    private static async Task<Dictionary<string, JsonElement>> CaptureAsync(KeycloakAdminClient admin, bool historicalAssignments = true)
    {
        var capture = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        async Task<JsonElement> ReadAsync(string path)
        {
            if (capture.TryGetValue(path, out var prior)) return prior;
            var value = await admin.GetAsync(path);
            capture.Add(path, value);
            return value;
        }

        await ReadAsync("");
        await ReadAsync("users/profile");
        var clients = await ReadAsync("clients");
        var scopes = await ReadAsync("client-scopes");
        var realmRoles = await ReadAsync("roles");
        await ReadAsync("groups");
        var accountSummary = Named(clients, "clientId", "account");
        var consoleSummary = Named(clients, "clientId", "account-console");
        var accountPath = "clients/" + Id(accountSummary);
        var consolePath = "clients/" + Id(consoleSummary);
        var account = await ReadAsync(accountPath);
        var console = await ReadAsync(consolePath);
        Bind(accountSummary, account, "clientId");
        Bind(consoleSummary, console, "clientId");
        foreach (var path in new[] { accountPath, consolePath })
        {
            await ReadAsync(path + "/default-client-scopes");
            await ReadAsync(path + "/optional-client-scopes");
        }
        if (historicalAssignments)
        {
            Names(capture[consolePath + "/default-client-scopes"]).Should().Equal(DefaultScopes);
            Names(capture[consolePath + "/optional-client-scopes"]).Should().Equal(OptionalScopes);
        }
        await ReadAsync(consolePath + "/protocol-mappers/models");
        await ReadAsync(consolePath + "/scope-mappings");
        var accountRoles = await ReadAsync(accountPath + "/roles");
        foreach (var summary in scopes.EnumerateArray())
        {
            var path = "client-scopes/" + Id(summary);
            var full = await ReadAsync(path);
            Bind(summary, full, "name");
            await ReadAsync(path + "/protocol-mappers/models");
            await ReadAsync(path + "/scope-mappings");
        }
        foreach (var category in new[] { "default", "optional" })
            foreach (var assignment in capture[consolePath + "/" + category + "-client-scopes"].EnumerateArray())
                Bind(assignment, Named(scopes, "name", Text(assignment, "name")), "name");
        await ReadAsync("roles/offline_access");
        foreach (var role in new[] { "manage-account", "manage-account-links", "view-groups" })
            await ReadAsync(accountPath + "/roles/" + role);
        var pending = new Queue<JsonElement>(realmRoles.EnumerateArray().Concat(accountRoles.EnumerateArray()));
        var readRoles = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var summary))
        {
            if (!readRoles.Add(Id(summary))) continue;
            if (readRoles.Count > 200) throw new InvalidOperationException("Stock proof role graph exceeded its fixed bound.");
            var path = "roles-by-id/" + Id(summary);
            var role = await ReadAsync(path);
            Bind(summary, role, "name");
            var composites = await ReadAsync(path + "/composites");
            foreach (var child in composites.EnumerateArray()) pending.Enqueue(child);
        }
        return capture;
    }

    private static async Task AssertExtractorAsync(string root, string capturePath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(root, "scripts", "extract-development-realm-stock-console.mjs"));
        start.ArgumentList.Add("--check");
        start.ArgumentList.Add(capturePath);
        var executablePath = Environment.GetEnvironmentVariable("PATH");
        start.Environment.Clear();
        if (executablePath is not null) start.Environment["PATH"] = executablePath;
        using var child = new Process { StartInfo = start };
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        var started = false;
        try
        {
            started = child.Start();
            if (!started) throw new InvalidOperationException("Stock proof extractor did not start.");
            stdout = ReadBoundedAsync(child.StandardOutput, deadline.Token);
            stderr = ReadBoundedAsync(child.StandardError, deadline.Token);
            await Task.WhenAll(child.WaitForExitAsync(deadline.Token), stdout, stderr);
            child.ExitCode.Should().Be(0, "the observed graph must match the independently committed fixed expectation");
            (await stderr).Length.Should().Be(0, "the extractor must emit only its closed success receipt");
            (await stdout == "stock-console: fixed expectation matched; sha256=" + CanonicalSha256 + "\n").Should().BeTrue(
                "the extractor must match the fixed canonical hash without publishing provider data");
        }
        finally
        {
            if (started)
            {
                if (!child.HasExited)
                    try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) when (child.HasExited) { }
                await child.WaitForExitAsync(CancellationToken.None);
                if (stdout is not null) try { await stdout; } catch (Exception) { }
                if (stderr is not null) try { await stderr; } catch (Exception) { }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1025];
        var count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(count), cancellationToken)) > 0)
        {
            count += read;
            if (count > 1024) throw new InvalidOperationException("Stock proof extractor output exceeded its fixed bound.");
        }
        return new string(buffer, 0, count);
    }

    private static async Task WritePrivateAsync(string path, byte[] bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes);
    }

    private static void Bind(JsonElement summary, JsonElement full, string property)
    {
        (Id(summary) == Id(full) && Text(summary, property) == Text(full, property)).Should().BeTrue(
            "independently read IDs and names must bind before any generated ID is canonicalized");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool Same(JsonElement left, JsonElement right) => JsonNode.DeepEquals(
        JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));
    private static bool SameSet(List<JsonElement> expected, JsonElement actual) => actual.GetArrayLength() == expected.Count
        && expected.All(value => actual.EnumerateArray().Count(candidate => Same(value, candidate)) == 1);
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()
        ?? throw new InvalidOperationException("Stock proof required field is absent.");
    private static string Id(JsonElement value) => Uri.EscapeDataString(Text(value, "id"));
    private static JsonElement Named(JsonElement values, string property, string name) => values.EnumerateArray()
        .Single(value => Text(value, property) == name);
    private static string[] Names(JsonElement values) => values.EnumerateArray().Select(value => Text(value, "name"))
        .Order(StringComparer.Ordinal).ToArray();

    private static async Task<string> HistoricalImportAsync(string root)
    {
        var path = Path.Combine(root, "backend", "tests", "LearnStack.Tests.Integration", "TenancyIdentity",
            "Fixtures", "learnstack-24705a7a.json");
        var bytes = await File.ReadAllBytesAsync(path);
        Hash(bytes).Should().Be(RedactedExportSha256, "only the reviewed credential-redacted historical fixture is imported");
        var realm = JsonNode.Parse(bytes)!.AsObject();
        var users = realm["users"]!.AsArray();
        users.Count.Should().Be(2);
        foreach (var user in users)
        {
            (user!["credentials"]![0]!["value"]!.GetValue<string>() == "__HISTORICAL_TEST_PASSWORD__").Should().BeTrue();
            user["credentials"]![0]!["value"] = Guid.NewGuid().ToString("N");
        }
        var api = realm["clients"]!.AsArray().Single(client => client!["clientId"]!.GetValue<string>() == "learnstack-api")!;
        (api["secret"]!.GetValue<string>() == "__HISTORICAL_TEST_API_SECRET__").Should().BeTrue();
        api["secret"] = Guid.NewGuid().ToString("N");
        return realm.ToJsonString();
    }
}
