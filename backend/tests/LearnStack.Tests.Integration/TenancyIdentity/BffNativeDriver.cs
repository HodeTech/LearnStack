using System.Diagnostics;
using System.Text.Json;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Private bounded pipe to one owned native Next process; no credentials in argv or results.</summary>
internal sealed class BffNativeDriver : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] ErrorStages = (new[] { "input", "input-issuer", "input-api", "input-secrets", "input-sites", "setup", "compile", "build", "start", "command", "request", "request-deadline", "request-connect", "request-tls", "request-socket", "response-json", "cleanup", "privacy", "native-stop",
                     "browser-launch", "browser-anonymous", "browser-signin-document", "browser-submit", "browser-origin-null", "browser-origin-absent", "browser-origin-other", "browser-storage", "browser-extension", "browser-logout-submit", "browser-logout-origin", "browser-logout-origin-null", "browser-logout-origin-other", "browser-logout-response", "browser-logout-location", "browser-logout-navigation", "browser-logout-cookie-header", "browser-logout-cookie", "browser-back-revalidation", "browser-history-setup", "browser-history-revalidation", "browser-headers",
                     "browser-executable", "browser-spawn", "browser-devtools", "browser-devtools-inactive",
                     "browser-devtools-unreadable", "browser-devtools-missing", "browser-devtools-empty", "browser-devtools-malformed",
                     "browser-exited", "browser-connect", "browser-context" }
                 .Concat(new[] { "browser-callback-", "browser-authorize-", "browser-logout-status-" }
                     .SelectMany(prefix => new[] { "0", "200", "303", "400", "403", "404", "429", "503", "other" }.Select(code => prefix + code)))).ToArray();
    private readonly Process _child;
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromMinutes(4));
    private readonly Task<string?> _errors;
    private readonly string _temporaryRoot = Directory.CreateTempSubdirectory("learnstack-bff-driver-").FullName;
    private Task? _disposal;
    private bool _complete;
    private int _expectedExit;

    private BffNativeDriver(string root, bool browser = false, bool counter = false, bool heldBuild = false)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        start.ArgumentList.Add(Path.Combine(root, "frontend/apps/web/scripts/" + (browser ? "verify-bff-browser.mjs" : "verify-bff-session.mjs")));
        if (counter) start.ArgumentList.Add(browser ? "--without-history" : "--without-successful-native-stop");
        if (heldBuild) start.ArgumentList.Add("--hold-build");
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "1";
        foreach (var key in new[] { "TMPDIR", "TMP", "TEMP" }) start.Environment[key] = _temporaryRoot;
        _child = new() { StartInfo = start };
        try
        {
            if (!_child.Start()) throw new InvalidOperationException();
        }
        catch
        {
            _deadline.Dispose();
            _child.Dispose();
            RemoveEmptyTemporaryRoot();
            throw new InvalidOperationException("BFF native fixture could not start.");
        }
        _errors = ReadErrorsAsync(_child.StandardError, CancellationToken.None);
    }

    internal string TemporaryRoot => _temporaryRoot;

    internal static async Task<BffNativeDriver> StartAsync(string root, object configuration, bool shutdownCounter = false, bool heldBuild = false,
        Action? secondaryCleanupFailure = null)
    {
        var driver = new BffNativeDriver(root, counter: shutdownCounter, heldBuild: heldBuild);
        try
        {
            await driver.WriteAsync(configuration);
            await driver.ReadAsync(heldBuild ? "held-build" : "ready");
            return driver;
        }
        catch (Exception error) when (Catchable(error))
        {
            try { await driver.DisposeAsync(); }
            catch (Exception cleanup) when (Catchable(cleanup))
            {
                try { secondaryCleanupFailure?.Invoke(); }
                catch (Exception observation) when (Catchable(observation)) { }
            }
            throw;
        }
    }

    internal static async Task<BffBrowserReceipt> BrowserAsync(string root, object configuration, bool counter,
        Action? secondaryCleanupFailure = null)
    {
        var driver = new BffNativeDriver(root, browser: true, counter: counter);
        var bodyFailed = false;
        try { return await ReadBrowserAsync(driver, configuration, counter); }
        catch (Exception error) when (Catchable(error)) { bodyFailed = true; throw; }
        finally
        {
            // Always attempt actual disposal. A second cleanup error must not
            // replace the first failure; cleanup-only failure still propagates.
            try { await driver.DisposeAsync(); }
            catch (Exception error) when (bodyFailed && Catchable(error))
            {
                try { secondaryCleanupFailure?.Invoke(); }
                catch (Exception observation) when (Catchable(observation)) { }
            }
        }
    }

    private static bool Catchable(Exception error) => error.GetType() != typeof(OutOfMemoryException)
        && error.GetType() != typeof(StackOverflowException);

    private static async Task<BffBrowserReceipt> ReadBrowserAsync(BffNativeDriver driver, object configuration, bool counter)
    {
        await driver.WriteAsync(configuration);
        var reply = await driver.ReadAsync("browser_result");
        driver._child.StandardInput.Close();
        await driver._child.WaitForExitAsync(driver._deadline.Token);
        await driver.AssertOutputEndedAsync();
        var expected = counter ? "rejected_history" : "passed";
        var version = reply.GetProperty("version").GetString();
        driver._expectedExit = counter ? 1 : 0;
        if (reply.GetProperty("outcome").GetString() != expected || reply.GetProperty("hosts").GetInt32() != 2
            || version is null || version.Length > 32 || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+(?:\.\d+){1,3}$")
            || driver._child.ExitCode != driver._expectedExit || await driver._errors is not null)
            throw new InvalidOperationException("BFF browser fixture lacks its complete positive/counter witness.");
        var restores = counter ? 0 : reply.GetProperty("naturalRestores").GetInt32();
        if (restores is < 0 or > 32) throw new InvalidOperationException("BFF browser restore count is outside its bound.");
        driver._complete = true;
        var witness = counter ? reply.GetProperty("witness").GetString() : null;
        if (counter && witness is not ("natural_back" or "persisted_event"))
            throw new InvalidOperationException("BFF browser counter has no closed restoration witness.");
        return new(version, restores, witness);
    }

    internal async Task<BffWireResponse> RequestAsync(string host, string path, string cookie = "",
        string method = "GET", string body = "")
    {
        await WriteAsync(new { protocol = 1, action = "request", host, path, cookie, method, body });
        var reply = await ReadAsync("response");
        try
        {
            var status = reply.GetProperty("status").GetInt32();
            var cookies = reply.GetProperty("cookies").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var location = reply.GetProperty("location").GetString();
            var cache = reply.GetProperty("cache").GetString();
            if (status is < 100 or > 599 || cookies.Length > 32 || cookies.Sum(value => value.Length) > 32768
                || (location?.Length ?? 0) > 32768) throw new InvalidOperationException();
            return new(status, cookies, location, cache, reply.GetProperty("data").Clone(),
                reply.GetProperty("rendered").GetBoolean(), reply.GetProperty("privateFree").GetBoolean());
        }
        catch { throw new InvalidOperationException("BFF native fixture response has an invalid closed shape."); }
    }

    internal async Task CompleteAsync()
    {
        await WriteAsync(new { protocol = 1, action = "finish" });
        await ReadAsync("complete");
        _child.StandardInput.Close();
        await _child.WaitForExitAsync(_deadline.Token);
        await AssertOutputEndedAsync();
        if (_child.ExitCode != 0 || await _errors is not null)
            throw new InvalidOperationException("BFF native fixture failed after owned cleanup.");
        _complete = true;
    }

    private async Task WriteAsync(object value)
    {
        try
        {
            await _child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, Json).AsMemory(), _deadline.Token);
            await _child.StandardInput.FlushAsync(_deadline.Token);
        }
        catch { throw new InvalidOperationException("BFF native fixture private input failed."); }
    }

    private async Task<JsonElement> ReadAsync(string phase)
    {
        var lastStage = "input";
        using var responseBudget = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        responseBudget.CancelAfter(TimeSpan.FromSeconds(phase == "ready" ? 90 : phase == "browser_result" ? 150 : 40));
        try
        {
            for (var count = 0; count < 8; ++count)
            {
                var line = await ReadBoundedLineAsync(responseBudget.Token);
                if (line is null)
                {
                    await _child.WaitForExitAsync(responseBudget.Token);
                    throw new InvalidOperationException();
                }
                using var parsed = JsonDocument.Parse(line);
                if (parsed.RootElement.GetProperty("protocol").GetInt32() != 1) throw new InvalidOperationException();
                var actual = parsed.RootElement.GetProperty("phase").GetString();
                if (phase is "ready" or "held-build" && actual == "progress")
                {
                    var value = parsed.RootElement.GetProperty("stage").GetString();
                    if (value is not ("compile" or "build" or "start")) throw new InvalidOperationException();
                    lastStage = value;
                    continue;
                }
                if (actual != phase) throw new InvalidOperationException();
                return parsed.RootElement.Clone();
            }
            throw new InvalidOperationException();
        }
        catch
        {
            var reason = "response";
            if (responseBudget.IsCancellationRequested) reason = "deadline_" + lastStage;
            else if (_child.HasExited) try { reason = await _errors ?? "exit"; } catch { reason = "output"; }
            throw new InvalidOperationException("BFF native fixture did not reach the closed " + phase + "/" + reason + " phase.");
        }
    }

    private async Task AssertOutputEndedAsync()
    {
        if (await ReadBoundedLineAsync(_deadline.Token) is not null)
            throw new InvalidOperationException("BFF native fixture emitted an unexpected extra receipt.");
    }

    private async Task<string?> ReadBoundedLineAsync(CancellationToken token)
    {
        var line = new System.Text.StringBuilder();
        var character = new char[1];
        while (await _child.StandardOutput.ReadAsync(character.AsMemory(), token) == 1)
        {
            if (character[0] == '\n') return line.ToString();
            if (line.Length == 65536) throw new InvalidOperationException("BFF native fixture receipt exceeds its bound.");
            line.Append(character[0]);
        }
        if (line.Length != 0) throw new InvalidOperationException("BFF native fixture receipt is truncated.");
        return null;
    }

    internal static async Task<string?> ReadErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        var overflow = false;
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            if (text.Length + read <= 256) text.Append(buffer, 0, read); else overflow = true;
        if (text.Length == 0 && !overflow) return null;
        foreach (var stage in ErrorStages)
            foreach (var prefix in new[] { "BFF session fixture failed: ", "BFF browser fixture failed: " })
            {
                var closed = prefix + stage + "\n";
                if (!overflow && text.ToString() == closed) return stage;
                var cleanup = prefix == "BFF browser fixture failed: "
                    ? "BFF browser cleanup failed.\n" : "BFF session cleanup failed.\n";
                if (!overflow && text.ToString() == closed + cleanup) return stage + "_cleanup_failed";
                if (text.ToString().Contains(closed, StringComparison.Ordinal)) return stage + "_unexpected_output";
            }
        foreach (var (pattern, reason) in new[] { ("ERR_MODULE_NOT_FOUND", "node_module_missing"),
                     ("SyntaxError", "node_syntax"), ("AbortError", "node_abort"), ("Permission denied", "node_permission") })
            if (text.ToString().Contains(pattern, StringComparison.Ordinal)) return reason;
        return "unexpected_output";
    }

    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var forced = false;
        try
        {
            // EOF is a private lifetime signal, not a request to kill Node before
            // it can join its own groups and remove the nested TLS/build tree.
            try { _child.StandardInput.Close(); }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            var drained = DrainOutputAsync(_child.StandardOutput, budget.Token);
            if (!_child.HasExited)
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                grace.CancelAfter(TimeSpan.FromSeconds(15));
                try { await _child.WaitForExitAsync(grace.Token); }
                catch (OperationCanceledException)
                {
                    forced = true;
                    try { _child.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (_child.HasExited) { }
                }
            }
            await _child.WaitForExitAsync(budget.Token);
            await Task.WhenAll(drained, _errors.WaitAsync(budget.Token));
            if (forced) throw new InvalidOperationException("BFF native fixture required forced cleanup.");
            if (_complete && _child.ExitCode != _expectedExit)
                throw new InvalidOperationException("BFF native fixture completed without a successful exit.");
            if (!RemoveEmptyTemporaryRoot())
                throw new InvalidOperationException("BFF native fixture retained its private TLS/build tree.");
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("BFF native fixture cleanup could not be confirmed."); }
        finally { _deadline.Dispose(); _child.Dispose(); }
    }

    private bool RemoveEmptyTemporaryRoot()
    {
        try
        {
            if (!Directory.Exists(_temporaryRoot)) return true;
            if (Directory.EnumerateFileSystemEntries(_temporaryRoot).Any()) return false;
            Directory.Delete(_temporaryRoot); // Never recursively hide incomplete child cleanup.
            return true;
        }
        catch { return false; }
    }

    private static async Task DrainOutputAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }
}

internal sealed record BffWireResponse(int Status, string[] Cookies, string? Location, string? Cache,
    JsonElement Data, bool Rendered, bool PrivateFree);
internal sealed record BffBrowserReceipt(string Version, int NaturalRestores, string? CounterWitness);

/// <summary>Separate HTTP-chain jars; browser storage rules are proved by the actual browser case.</summary>
internal sealed class BffWireCookies
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    internal string Header => string.Join("; ", _values.Select(value => value.Key + "=" + value.Value));
    internal void Apply(BffWireResponse response)
    {
        foreach (var cookie in response.Cookies)
        {
            var pair = cookie.Split(';', 2)[0];
            var equal = pair.IndexOf('=');
            if (equal < 1) throw new InvalidOperationException("BFF native fixture cookie is malformed.");
            var name = pair[..equal];
            var value = pair[(equal + 1)..];
            if (value.Length == 0) _values.Remove(name);
            else _values[name] = value;
        }
    }
}
