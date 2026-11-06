using Microsoft.AspNetCore.Hosting;

namespace LearnStack.Tests.Fixtures;

/// <summary>Explicit synthetic construction settings; never a production fallback or SMTP connection.</summary>
internal static class InvitationEmailFixtureConfiguration
{
    internal static IReadOnlyDictionary<string, string?> Values { get; } = new Dictionary<string, string?>
    {
        ["InvitationEmail:KeyRing"] = "{\"active\":\"test\",\"keys\":{\"test\":\"AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=\"}}",
        ["InvitationEmail:Smtp:Host"] = "smtp.example.invalid",
        ["InvitationEmail:Smtp:Port"] = "465",
        ["InvitationEmail:Smtp:Sender"] = "fixture@example.invalid",
        ["InvitationEmail:Smtp:Tls"] = "ImplicitTls",
        ["InvitationEmail:Smtp:Username"] = "synthetic-fixture",
        ["InvitationEmail:Smtp:Password"] = "synthetic-fixture",
    };

    internal static void AddTo(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var (key, value) in Values) builder.UseSetting(key, value);
    }
}
