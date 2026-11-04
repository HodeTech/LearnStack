using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Closed parent EOF bridge for the actual held Next preparation case.</summary>
internal sealed class FrameworkOwnershipDriver : IAsyncDisposable
{
    private readonly Process _child;
    private readonly CancellationTokenSource _deadline;
    private readonly string _temporaryRoot;
    private readonly Task<bool> _stderr;
    private Task? _disposal;
    private bool _complete;
    private bool _eofDelivered;

    private FrameworkOwnershipDriver(string repositoryRoot)
    {
        var script = Path.Combine(repositoryRoot, "frontend/apps/web/scripts/verify-framework-runtime-ownership.mjs");
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!File.Exists(script) || string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Framework ownership fixture source is unavailable.");
        _temporaryRoot = Directory.CreateTempSubdirectory("learnstack-framework-owner-driver-").FullName;
        _deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("prepare-parent-eof");
        start.ArgumentList.Add("--parent-pipe");
        start.Environment.Clear();
        start.Environment["PATH"] = path;
        start.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "1";
        foreach (var key in new[] { "TMPDIR", "TMP", "TEMP" }) start.Environment[key] = _temporaryRoot;
        _child = new Process { StartInfo = start };
        try
        {
            if (!_child.Start()) throw new InvalidOperationException();
        }
        catch
        {
            _deadline.Dispose();
            _child.Dispose();
            RemoveEmptyTemporaryRoot();
            throw new InvalidOperationException("Framework ownership fixture could not start.");
        }
        _stderr = ReadStderrAsync(_child.StandardError);
    }

    internal static async Task ProveParentEofAsync(string repositoryRoot)
    {
        var driver = new FrameworkOwnershipDriver(repositoryRoot);
        Exception? original = null;
        try { await driver.ProveAsync(); }
        catch (OperationCanceledException)
        {
            original = new InvalidOperationException(driver._eofDelivered
            ? "Framework ownership fixture exceeded its EOF grace."
            : "Framework ownership fixture exceeded its overall deadline.");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { original = error; }
        try { await driver.DisposeAsync(); }
        catch (Exception error) when (original is not null && error is not OutOfMemoryException and not StackOverflowException)
        { original.Data["FrameworkOwnershipCleanup"] = "failed"; }
        if (original is not null) ExceptionDispatchInfo.Capture(original).Throw();
    }

    private async Task ProveAsync()
    {
        try
        {
            await _child.StandardInput.WriteAsync("{\"protocol\":1,\"command\":\"start\"}\n".AsMemory(), _deadline.Token);
            await _child.StandardInput.FlushAsync(_deadline.Token);
        }
        catch { throw new InvalidOperationException("Framework ownership fixture start input failed."); }
        await RequireFrameAsync("prepare-held", _deadline.Token);
        if (_child.HasExited || !HasPrivateEntries())
            throw new InvalidOperationException("Framework ownership fixture lacked a live held preparation.");
        try { _child.StandardInput.Close(); } // EOF, never a PID or process-group signal.
        catch { throw new InvalidOperationException("Framework ownership fixture EOF input failed."); }
        _eofDelivered = true;
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        grace.CancelAfter(TimeSpan.FromSeconds(15));
        await RequireFrameAsync("closed", grace.Token);
        try { await _child.WaitForExitAsync(grace.Token); }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Framework ownership fixture exceeded its EOF grace."); }
        if (_child.ExitCode != 1)
            throw new InvalidOperationException("Framework ownership fixture had an unexpected exit.");
        string? extra;
        try { extra = await ReadBoundedLineAsync(grace.Token); }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Framework ownership fixture exceeded its EOF grace."); }
        if (extra is not null)
            throw new InvalidOperationException("Framework ownership fixture emitted an unexpected extra frame.");
        bool stderr;
        try { stderr = await _stderr.WaitAsync(grace.Token); }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Framework ownership fixture exceeded its EOF grace."); }
        if (stderr)
            throw new InvalidOperationException("Framework ownership fixture emitted unexpected stderr.");
        if (HasPrivateEntries())
            throw new InvalidOperationException("Framework ownership fixture retained its private tree.");
        _complete = true;
    }

    private async Task RequireFrameAsync(string phase, CancellationToken token)
    {
        try
        {
            var line = await ReadBoundedLineAsync(token);
            if (line is null) throw new InvalidOperationException();
            using var document = JsonDocument.Parse(line);
            var frame = document.RootElement;
            var names = string.Join(',', frame.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            if (frame.GetProperty("protocol").GetInt32() != 1 || frame.GetProperty("phase").GetString() != phase)
                throw new InvalidOperationException();
            if (phase == "prepare-held" && names != "phase,protocol" ||
                phase == "closed" && (names != "cleanup,ownerCode,phase,protocol,retired" ||
                    frame.GetProperty("ownerCode").GetInt32() != 1 ||
                    !frame.GetProperty("retired").GetBoolean() || !frame.GetProperty("cleanup").GetBoolean()))
                throw new InvalidOperationException();
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(phase == "closed"
            ? "Framework ownership fixture exceeded its EOF grace."
            : "Framework ownership fixture exceeded its overall deadline.");
        }
        catch (InvalidOperationException error) when (error.Message is
            "Framework ownership fixture output transport failed." or
            "Framework ownership fixture frame exceeded its bound." or
            "Framework ownership fixture frame was truncated.")
        { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { throw new InvalidOperationException("Framework ownership fixture rejected its " + phase + " frame."); }
    }

    private async Task<string?> ReadBoundedLineAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            int count;
            try { count = await _child.StandardOutput.ReadAsync(character.AsMemory(), token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            { throw new InvalidOperationException("Framework ownership fixture output transport failed."); }
            if (count == 0) break;
            if (character[0] == '\n') return line.ToString();
            if (line.Length == 512) throw new InvalidOperationException("Framework ownership fixture frame exceeded its bound.");
            line.Append(character[0]);
        }
        if (line.Length != 0) throw new InvalidOperationException("Framework ownership fixture frame was truncated.");
        return null;
    }

    private static async Task<bool> ReadStderrAsync(StreamReader reader)
    {
        try
        {
            var buffer = new char[1024];
            var observed = false;
            while (await reader.ReadAsync(buffer.AsMemory()) != 0) observed = true;
            return observed;
        }
        catch { return true; }
    }

    private bool HasPrivateEntries()
    {
        try
        {
            if (!Directory.Exists(_temporaryRoot) ||
                (File.GetAttributes(_temporaryRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException();
            return Directory.EnumerateFileSystemEntries(_temporaryRoot).Any();
        }
        catch { throw new InvalidOperationException("Framework ownership fixture private tree could not be inspected."); }
    }

    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var forced = false;
        try
        {
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
            await Task.WhenAll(drained, _stderr.WaitAsync(budget.Token));
            if (forced) throw new InvalidOperationException("Framework ownership fixture required forced cleanup.");
            if (_complete && _child.ExitCode != 1)
                throw new InvalidOperationException("Framework ownership fixture lost its expected failed exit.");
            if (!RemoveEmptyTemporaryRoot())
                throw new InvalidOperationException("Framework ownership fixture retained its private tree.");
        }
        catch (OperationCanceledException)
        { throw new InvalidOperationException("Framework ownership fixture cleanup could not be confirmed."); }
        finally { _deadline.Dispose(); _child.Dispose(); }
    }

    private bool RemoveEmptyTemporaryRoot()
    {
        try
        {
            if (!Directory.Exists(_temporaryRoot) ||
                (File.GetAttributes(_temporaryRoot) & FileAttributes.ReparsePoint) != 0) return false;
            if (Directory.EnumerateFileSystemEntries(_temporaryRoot).Any()) return false;
            Directory.Delete(_temporaryRoot);
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
