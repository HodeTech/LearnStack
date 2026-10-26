using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentitySchemaTests
{
    // Deliberately bounded to the installed named-input functions. Unsupported
    // declarations fail instead of vanishing from the effective function census.
    private static Match[] EffectiveFunctionDeclarations(string sql)
    {
        var declarations = FunctionDeclaration().Matches(sql).ToArray();
        if (FunctionMutationStart().Matches(sql).Any(start => !declarations.Any(declaration => declaration.Index == start.Index)))
            throw new FormatException("Unsupported function declaration or removal.");

        var effective = new Dictionary<string, Match>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            var parameters = FunctionParameters(declaration);
            var signature = declaration.Groups["name"].Value.ToLowerInvariant() + "("
                + string.Join(',', parameters.Select(parameter => parameter[(parameter.IndexOf(' ') + 1)..])) + ")";
            var exists = effective.TryGetValue(signature, out var previous);
            if (declaration.Groups["replace"].Success)
            {
                if (!exists)
                    throw new FormatException("Replacement has no earlier function declaration.");
                if (!parameters.SequenceEqual(FunctionParameters(previous!))
                    || !string.Equals(declaration.Groups["returns"].Value, previous!.Groups["returns"].Value, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Replacement changes the function arguments or return type.");
            }
            else if (exists)
            {
                throw new FormatException("Duplicate CREATE FUNCTION declaration.");
            }
            effective[signature] = declaration;
        }
        return effective.Values.ToArray();
    }

    private static string[] FunctionParameters(Match declaration)
    {
        var parameters = declaration.Groups["parameters"].Value.Trim();
        if (parameters.Length == 0) return [];
        return parameters.Split(',').Select(parameter =>
        {
            var normalized = Regex.Replace(parameter.Trim(), @"\s+", " ").ToLowerInvariant();
            if (!FunctionParameter().IsMatch(normalized))
                throw new FormatException("Unsupported function parameter declaration.");
            return normalized.Replace(" timestamptz", " timestamp with time zone", StringComparison.Ordinal);
        }).ToArray();
    }

    [Fact]
    public void Compiled_function_reader_uses_the_last_replacement_of_the_same_normalized_signature()
    {
        const string original = """
            CREATE FUNCTION public.fn_replacement_probe(at timestamptz) RETURNS boolean
            LANGUAGE sql SECURITY INVOKER SET search_path=pg_catalog AS $$ SELECT false $$;
            """;
        const string first = """
            CREATE OR REPLACE FUNCTION public.fn_replacement_probe(at timestamp with time zone) RETURNS boolean
            LANGUAGE sql SECURITY INVOKER SET search_path=pg_catalog AS $$ SELECT true $$;
            """;
        const string last = """
            CREATE OR REPLACE FUNCTION PUBLIC.FN_REPLACEMENT_PROBE( at   TIMESTAMPTZ ) RETURNS boolean
            LANGUAGE sql SECURITY INVOKER SET search_path=pg_catalog AS $$ SELECT at IS NOT NULL $$;
            """;

        EffectiveFunctionDeclarations(original).Single().Groups["body"].Value.Trim().Should().Be("SELECT false");
        EffectiveFunctionDeclarations(original + first).Single().Groups["body"].Value.Trim().Should().Be("SELECT true");
        EffectiveFunctionDeclarations(original + first + last).Single().Groups["body"].Value.Trim().Should().Be("SELECT at IS NOT NULL");
        EffectiveFunctionDeclarations(original + last + first).Single().Groups["body"].Value.Trim().Should().Be("SELECT true",
            "migration order, not declaration spelling or the first match, determines the effective body");
    }

    [Theory]
    [InlineData("duplicate-create", "Duplicate CREATE FUNCTION declaration.")]
    [InlineData("unknown-replacement", "Replacement has no earlier function declaration.")]
    [InlineData("changed-input-type", "Replacement has no earlier function declaration.")]
    [InlineData("changed-input-name", "Replacement changes the function arguments or return type.")]
    [InlineData("changed-return-type", "Replacement changes the function arguments or return type.")]
    [InlineData("unqualified-function", "Unsupported function declaration or removal.")]
    [InlineData("default-argument", "Unsupported function parameter declaration.")]
    [InlineData("drop-function", "Unsupported function declaration or removal.")]
    public void Compiled_function_reader_rejects_unsupported_replacements_and_a_clean_twin_passes(string fault, string expected)
    {
        const string original = """
            CREATE FUNCTION public.fn_replacement_probe(target uuid) RETURNS boolean
            LANGUAGE sql SECURITY INVOKER SET search_path=pg_catalog AS $$ SELECT false $$;
            """;
        var replacement = original.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
            .Replace("SELECT false", "SELECT true", StringComparison.Ordinal);
        var mutation = fault switch
        {
            "duplicate-create" => original,
            "unknown-replacement" => replacement.Replace("fn_replacement_probe", "fn_unknown_probe", StringComparison.Ordinal),
            "changed-input-type" => replacement.Replace("target uuid", "target text", StringComparison.Ordinal),
            "changed-input-name" => replacement.Replace("target uuid", "other uuid", StringComparison.Ordinal),
            "changed-return-type" => replacement.Replace("RETURNS boolean", "RETURNS bigint", StringComparison.Ordinal),
            "unqualified-function" => replacement.Replace("public.", "", StringComparison.Ordinal),
            "default-argument" => replacement.Replace("target uuid", "target uuid DEFAULT NULL", StringComparison.Ordinal),
            "drop-function" => "DROP FUNCTION public.fn_replacement_probe(uuid);",
            _ => throw new InvalidOperationException("Unknown planted function declaration fault."),
        };
        var parse = () => EffectiveFunctionDeclarations(original + mutation);
        parse.Should().Throw<FormatException>().WithMessage(expected);
        EffectiveFunctionDeclarations(original + replacement).Single().Groups["body"].Value.Trim().Should().Be("SELECT true");
    }

    [GeneratedRegex(@"\b(?:CREATE\s+(?:OR\s+REPLACE\s+)?(?:FUNCTION|PROCEDURE)|DROP\s+(?:FUNCTION|PROCEDURE))\b", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionMutationStart();

    [GeneratedRegex(@"\A[a-z_][a-z0-9_]* (?:uuid|bytea|text|boolean|bigint|integer|timestamptz|timestamp with time zone)\z", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionParameter();
}
