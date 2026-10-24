using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;
using FluentAssertions;
using LearnStack.Analyzers;
using LearnStack.SharedKernel.Secrets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace LearnStack.Tests.Unit.Analyzers;

public sealed class SensitiveExceptionMessageAnalyzerTests
{
    private const string FixedMessage =
        "Use a fixed exception message without sensitive values; keep sensitive data out of exception constructor strings";

    private const string Preamble = """
        using System;
        using Failure = System.InvalidOperationException;

        public sealed class ProviderFailure : Exception
        {
            public ProviderFailure(string provider, string message, bool client = false) : base(message) { }
        }

        public sealed class Sample
        {
            public string ClientSecret = "example";
            public string AuthHeader { get; } = "example";
            public const string SigningKey = "example";
            public const string Ordinary = "example";
            public string PasswordMethod() => "example";
            public static string Format(string value) => value;

        """;

    [Theory]
    [InlineData("public void Run(string accessToken) { throw new Exception(accessToken); }")]
    [InlineData("public void Run(string accessToken) => throw new InvalidOperationException(accessToken);")]
    [InlineData("public string Run(string accessToken) => accessToken ?? throw new Exception(accessToken);")]
    [InlineData("public void Run(string accessToken) => throw new Failure(accessToken);")]
    [InlineData("public void Run(string accessToken) => throw (Exception)new(accessToken);")]
    [InlineData("public void Run(string accessToken) => throw ((Exception)(object)new Failure((string)(object)accessToken));")]
    [InlineData("public void Run(object accessToken) => throw new Exception((string)accessToken);")]
    [InlineData("public void Run(string accessToken) => throw new Exception($\"Failure: {accessToken}\");")]
    [InlineData("public void Run(string accessToken) => throw new Exception(\"Failure: \" + accessToken);")]
    [InlineData("public void Run(string accessToken) => throw new Exception(accessToken + 42);")]
    [InlineData("public void Run(string accessToken) => throw new Exception(accessToken, (dynamic)null!);")]
    [InlineData("public void Run(string accessToken) => throw new Exception((dynamic)accessToken);")]
    [InlineData("public void Run(dynamic accessToken) => throw new Exception((string)accessToken, (dynamic)null!);")]
    [InlineData("public void Run(string accessToken) => throw new Exception($\"Length: {accessToken.Length}\");")]
    [InlineData("public void Run(string accessToken) => throw new Exception($\"{Format(accessToken)}\");")]
    [InlineData("public void Run() => throw new Exception(ClientSecret);")]
    [InlineData("public void Run() => throw new Exception(AuthHeader);")]
    [InlineData("public void Run() => throw new Exception(SigningKey);")]
    [InlineData("public void Run() { const string apiKey = \"example\"; throw new Exception(apiKey); }")]
    [InlineData("public void Run() { string setCookie = \"example\"; throw new Exception(setCookie); }")]
    [InlineData("public void Run(string accessToken) => throw new ProviderFailure(\"provider\", accessToken);")]
    [InlineData("public void Run(string accessToken) => throw new ProviderFailure(message: accessToken, provider: \"provider\");")]
    [InlineData("public void Run(string accessToken) => throw new ProviderFailure(accessToken, \"fixed\");")]
    [InlineData("public void Run(string accessToken) => throw new ProviderFailure(accessToken, $\"{ClientSecret}\");")]
    public async Task Reports_One_Fixed_Warning_At_The_Thrown_Creation(string member)
    {
        var source = Preamble + member + "\n}";
        var diagnostics = await AnalyzerCompilation.RunAsync(source, new SensitiveExceptionMessageAnalyzer());
        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be("LS0002");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Descriptor.Category.Should().Be("Security");
        diagnostic.Descriptor.IsEnabledByDefault.Should().BeTrue();
        diagnostic.Descriptor.Title.ToString(CultureInfo.InvariantCulture)
            .Should().Contain("LearnStackException-SensitiveExceptionMessage");
        diagnostic.GetMessage(CultureInfo.InvariantCulture).Should().Be(FixedMessage);

        var creation = diagnostic.Location.SourceTree!.GetRoot().DescendantNodes()
            .Single(node => node is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax);
        diagnostic.Location.SourceSpan.Should().Be(creation.Span);
        diagnostic.Location.GetLineSpan().Path.Should().Be("Sample.cs");
    }

    [Theory]
    [InlineData("public void Run(string className) => throw new Exception(className);")]
    [InlineData("public void Run(string className) => throw new Exception(className, (dynamic)null!);")]
    [InlineData("public void Run(string panel) => throw new Exception($\"{panel}\");")]
    [InlineData("public void Run() => throw new Exception(\"password: secret\");")]
    [InlineData("public void Run() => throw new Exception(Ordinary);")]
    [InlineData("public void Run() { const string ordinary = \"password\"; throw new Exception(ordinary); }")]
    [InlineData("public void Run(string accessToken) => throw new Exception(nameof(accessToken));")]
    [InlineData("public void Run(string accessToken) => throw new Exception($\"{nameof(accessToken)} {typeof(Sample)}\");")]
    [InlineData("public void Run() => throw new Exception($\"{PasswordMethod()}\");")]
    [InlineData("public void Run(string accessToken) => throw new Exception(Format(accessToken));")]
    [InlineData("public void Run(string accessToken, bool choose) => throw new Exception(choose ? accessToken : \"fixed\");")]
    [InlineData("public Exception Run(string accessToken) => new Exception($\"{accessToken}\");")]
    [InlineData("public void Run(string accessToken) { var failure = new Exception(accessToken); throw failure; }")]
    [InlineData("public void Run(string accessToken) => throw new Exception($\"{(Func<string>)(() => accessToken)}\");")]
    [InlineData("public void Run(string accessToken) => throw new Exception(\"fixed\" + (Func<string>)(() => accessToken));")]
    [InlineData("public void Run(string accessToken) => throw new Exception($\"{((Func<string>)(() => accessToken))()}\");")]
    [InlineData("public void Run(System.Threading.CancellationToken accessToken) => throw new OperationCanceledException(\"fixed\", accessToken);")]
    [InlineData("public void Run(System.Collections.Generic.Dictionary<string, string> values) => throw new Exception($\"{values[\"token\"]}\");")]
    public async Task Keeps_Excluded_Forms_And_Ordinary_References_Clean(string member)
    {
        var diagnostics = await AnalyzerCompilation.RunAsync(Preamble + member + "\n}", new SensitiveExceptionMessageAnalyzer());
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task User_Defined_String_Addition_Is_Outside_The_Initial_Root_Forms()
    {
        const string source = """
            using System;
            public sealed class SecretException
            {
                public static string operator +(SecretException left, string right) => right;
            }
            public static class Sample
            {
                public static void Run(SecretException secret, string value) =>
                    throw new Exception(secret + value);
            }
            """;

        (await AnalyzerCompilation.RunAsync(source, new SensitiveExceptionMessageAnalyzer())).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Sample.g.cs", "")]
    [InlineData("Sample.cs", "// <auto-generated/>\n")]
    public async Task Generated_Code_Is_Excluded(string path, string header)
    {
        var source = header + Preamble + "public void Run(string token) => throw new Exception(token);\n}";
        (await AnalyzerCompilation.RunAsync(source, new SensitiveExceptionMessageAnalyzer(), path: path)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData("FALSE", 0)]
    public async Task LS0001_Preserves_Its_Explicit_Project_Scope(string scope, int expected)
    {
        const string source = """
            namespace LearnStack.SharedKernel.Errors
            {
                public class DomainException : System.Exception { }
            }
            public static class Sample
            {
                public static void Run() => throw new LearnStack.SharedKernel.Errors.DomainException();
            }
            """;
        var options = new AnalyzerOptions([], new AnalyzerCompilation.DomainScopeOptions(scope));
        var diagnostics = await AnalyzerCompilation.RunAsync(source, new DomainExceptionThrowAnalyzer(), options);
        diagnostics.Count(diagnostic => diagnostic.Id == "LS0001").Should().Be(expected);
    }

    [Fact]
    public async Task Loaded_Analyzer_Uses_The_Complete_Runtime_Token_Union()
    {
        var matcher = typeof(SensitiveExceptionMessageAnalyzer).Assembly.GetType(
            "LearnStack.SharedKernel.Secrets.SensitiveTokenMatcher", throwOnError: true)!;
        var analyzerTokens = (IReadOnlyCollection<string>)matcher.GetProperty(
            "DefaultTokens", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var runtimeTokens = SensitiveTokenCatalog.DefaultTokens;
        analyzerTokens.Should().BeEquivalentTo(runtimeTokens);
        analyzerTokens.Should().BeAssignableTo<ICollection<string>>().Subject.IsReadOnly.Should().BeTrue();

        foreach (var token in runtimeTokens)
        {
            var identifier = "probe_" + token;
            var source = $$"""
                public static class Sample
                {
                    public static void Run(string {{identifier}}) => throw new System.Exception({{identifier}});
                }
                """;
            var diagnostic = (await AnalyzerCompilation.RunAsync(source, new SensitiveExceptionMessageAnalyzer()))
                .Should().ContainSingle("the loaded analyzer must recognize runtime token '{0}'", token).Subject;
            diagnostic.Id.Should().Be("LS0002");
            diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
            diagnostic.GetMessage(CultureInfo.InvariantCulture).Should().Be(FixedMessage);
        }
    }

    [Fact]
    public async Task Two_Word_Only_Source_Addition_Reaches_Runtime_And_The_Compiled_Analyzer()
    {
        const string token = "boundaryprobe";
        const string identifier = "BoundaryProbe";
        SensitiveTokenCatalog.IsSensitive(identifier).Should().BeFalse();
        const string sample = """
            public static class Sample
            {
                public static void Run(string BoundaryProbe) => throw new System.Exception(BoundaryProbe);
            }
            """;
        (await AnalyzerCompilation.RunAsync(sample, new SensitiveExceptionMessageAnalyzer())).Should().BeEmpty();

        var root = AnalyzerCompilation.RepositoryRoot();
        var matcherPath = Path.Combine(root, "backend/src/LearnStack.SharedKernel/Secrets/SensitiveTokenMatcher.cs");
        var matcher = File.ReadAllText(matcherPath);
        matcher.Should().NotContain('"' + token + '"');
        var secondSet = matcher.IndexOf("TwoWordTokens =", StringComparison.Ordinal);
        secondSet.Should().BeGreaterThan(0);
        var initializer = matcher.IndexOf('{', secondSet);
        initializer.Should().BeGreaterThan(secondSet);
        var mutated = matcher.Insert(initializer + 1, "\n            \"" + token + "\",");

        var runtimeSources = new[]
        {
            (mutated, matcherPath),
            (File.ReadAllText(Path.Combine(root, "backend/src/LearnStack.SharedKernel/Secrets/SensitiveTokenCatalog.cs")), "SensitiveTokenCatalog.cs"),
        };
        var directory = Path.Combine(root, "backend/analyzers/LearnStack.Analyzers");
        var project = XDocument.Load(Path.Combine(directory, "LearnStack.Analyzers.csproj"));
        var linked = project.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(path => Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar), directory)).ToArray();
        linked.Should().ContainSingle(path => path == matcherPath,
            "the actual analyzer project must compile the canonical matcher source");
        var analyzerSources = Directory.EnumerateFiles(directory, "*.cs")
            .Concat(linked)
            .Select(path => (path == matcherPath ? mutated : File.ReadAllText(path), path)).ToArray();

        var context = new AssemblyLoadContext("SensitiveTokenPropagation_" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var runtime = AnalyzerCompilation.Load(AnalyzerCompilation.Create(runtimeSources), context);
            var facade = runtime.GetType("LearnStack.SharedKernel.Secrets.SensitiveTokenCatalog", throwOnError: true)!;
            facade.GetMethod("IsSensitive", BindingFlags.Static | BindingFlags.Public)!
                .Invoke(null, [identifier]).Should().Be(true);
            ((IReadOnlyCollection<string>)facade.GetProperty("DefaultTokens")!.GetValue(null)!).Should().Contain(token);

            var settings = AnalyzerCompilation.EvaluatedAnalyzerSettings(Path.Combine(directory, "LearnStack.Analyzers.csproj"));
            var assembly = AnalyzerCompilation.Load(
                AnalyzerCompilation.CreateWithSettings(settings.References, settings.Options, analyzerSources), context);
            var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(
                assembly.GetType(typeof(SensitiveExceptionMessageAnalyzer).FullName!, throwOnError: true)!)!;
            var diagnostic = (await AnalyzerCompilation.RunAsync(sample, analyzer)).Should().ContainSingle().Subject;
            diagnostic.Id.Should().Be("LS0002");
            diagnostic.GetMessage(CultureInfo.InvariantCulture).Should().Be(FixedMessage);
        }
        finally
        {
            context.Unload();
        }
    }
}
