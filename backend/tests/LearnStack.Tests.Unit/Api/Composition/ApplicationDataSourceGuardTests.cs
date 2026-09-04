using FluentAssertions;
using LearnStack.Api.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

/// <summary>
/// What <c>ConnectionStrings:Default</c> is allowed to name.
/// </summary>
/// <remarks>
/// <para>
/// This is the single control standing between a pasted connection string and
/// every Row Level Security policy in the database going inert. The two
/// <c>BYPASSRLS</c> roles sit two and three lines away from <c>Default</c> in
/// <c>.env.example</c>, and with either of them here Packet 6's fail-closed
/// state — an unresolved tenant context, so <c>app.tenant_id = ''</c> — stops
/// returning no rows and starts returning every tenant's.
/// </para>
/// <para>
/// The symmetric guard for the migration credential has existed since Packet 6
/// step 3, in the <c>migrate</c> target. This is the runtime half, and it was
/// missing: the composition root argued for <c>learnstack_app</c> in two
/// paragraphs of remarks and then built a data source from whatever the key held.
/// </para>
/// </remarks>
public sealed class ApplicationDataSourceGuardTests
{
    // Every literal below is an INPUT to the guard under test — refusing or
    // accepting connection strings is the whole of what it does, so the file
    // cannot be written without them. They name localhost and a password no
    // service has ever had. leakwatch:ignore applies per line.
    private const string Valid =
        "Host=localhost;Port=5432;Database=learnstack;Username=learnstack_app;Password=s3cret"; // leakwatch:ignore

    [Fact]
    public void The_application_role_is_accepted()
    {
        // Building the data source opens nothing — the physical-connection check
        // that asks the server about rolbypassrls runs on first connect, and is
        // covered against a real database in the integration suite.
        var build = () => PersistenceCompositionExtensions.BuildApplicationDataSource(Valid);

        build.Should().NotThrow();
    }

    [Theory]
    [InlineData("learnstack_migration")]
    [InlineData("learnstack_platform")]
    [InlineData("learnstack_outbox_admin")]
    [InlineData("postgres")]
    public void Any_other_role_is_refused_by_name(string role)
    {
        var build = () => PersistenceCompositionExtensions.BuildApplicationDataSource(
            $"Host=localhost;Database=learnstack;Username={role};Password=s3cret"); // leakwatch:ignore

        build.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Username='{role}'*")
            .And.Message.Should().Contain("learnstack_app");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_absent_value_names_the_key_it_is_missing_from(string? value)
    {
        var build = () => PersistenceCompositionExtensions.BuildApplicationDataSource(value);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:Default*");
    }

    [Theory]
    // A URI-style DSN is the likely malformed value: it is the form DATABASE_URL
    // carries on several hosts. Npgsql's own exception names neither the key nor
    // the expected form.
    [InlineData("postgres://learnstack_app:pw@localhost:5432/learnstack")] // leakwatch:ignore
    [InlineData("Host=localhost;Port=not-a-number;Username=learnstack_app")]
    public void A_malformed_value_names_the_key_and_the_expected_form(string value)
    {
        var build = () => PersistenceCompositionExtensions.BuildApplicationDataSource(value);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:Default*")
            .And.Message.Should().Contain("key/value");
    }

    [Fact]
    public void No_message_carries_the_password()
    {
        // This is the one place a runtime credential is read. An error that echoed
        // it would put it in every log that captured the startup failure — the
        // mistake the migrate target already made once and fixed.
        var messages = new List<string>();

        // Pwd and PSW are Npgsql aliases for Password and parse into the same
        // field. A keyword regex that knew only the canonical spelling carried
        // both straight into the message — measured, which is why the redaction
        // now clears the parsed field rather than matching text.
        foreach (var value in new[]
        {
            "Host=localhost;Username=learnstack_platform;Password=hunter2", // leakwatch:ignore
            "Host=localhost;Username=learnstack_platform;Pwd=hunter2", // leakwatch:ignore
            "Host=localhost;Username=learnstack_platform;PSW=hunter2", // leakwatch:ignore
            "Host=localhost;Port=nope;Username=learnstack_app;Password=hunter2", // leakwatch:ignore
            "Host=localhost;Port=nope;Username=learnstack_app;Pwd=hunter2", // leakwatch:ignore
            // The URI form carries its password in the userinfo, where no
            // `password=` appears for a keyword regex to find — and Npgsql rejects
            // the form outright, so this branch is exactly where it lands. The
            // first version of the redaction echoed it whole.
            "postgres://learnstack_app:hunter2@localhost:5432/learnstack", // leakwatch:ignore
            "postgresql://learnstack_app:hunter2@localhost/learnstack?sslmode=require", // leakwatch:ignore

            // The two shapes the userinfo pattern could not span. It was
            // `(://)[^/@\s]*@`, and a character class excluding '/' and '@' stops at
            // the first one inside the password — so a password containing either
            // reached the message whole. Reserved characters in a password are legal
            // and common; these are the canaries for it.
            "postgres://learnstack_app:hunter2/extra@localhost:5432/learnstack", // leakwatch:ignore
            "postgres://learnstack_app:hunter2@more@localhost:5432/learnstack", // leakwatch:ignore
        })
        {
            try
            {
                PersistenceCompositionExtensions.BuildApplicationDataSource(value);
            }
            catch (InvalidOperationException exception)
            {
                messages.Add(exception.Message);
            }
        }

        messages.Should().HaveCount(9, "every value is refused");
        messages.Should().OnlyContain(message => !message.Contains("hunter2", StringComparison.Ordinal));

        // No "***" assertion any more, and its absence is the fix. A redacted echo is
        // only as good as the pattern that redacts it, and two of the values above
        // defeated the pattern. The unparseable branch now repeats nothing at all —
        // there is no field to be confident about — so what the message must carry is
        // the key and the expected form, not a masked version of the secret.
        messages.Should().OnlyContain(
            message => message.Contains("ConnectionStrings:Default", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("learnstack_migration")]
    [InlineData("learnstack_platform")]
    [InlineData("learnstack_outbox_admin")]
    public void A_present_but_wrong_credential_refuses_the_boot_at_registration(string role)
    {
        // The eager half, and the one the other cases here cannot see: they all
        // call BuildApplicationDataSource directly, which validates whether or not
        // the caller ever reaches it, so every one of them passes with the
        // composition root left purely lazy. Measured three times — deleting the
        // eager block leaves the whole 1026-test suite green.
        //
        // What is being asserted is WHEN: the throw comes out of
        // AddLearnStackPersistence itself, before any ServiceProvider is built and
        // long before the first request. The Lazy<NpgsqlDataSource> that keeps a
        // platform-only deployment from needing a credential at all was allowed to
        // defer the build; it was not meant to defer the checks with it, because
        // a string naming learnstack_migration is the ownership mistake FORCE ROW
        // LEVEL SECURITY exists to defeat and production is a bad place to find it.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] =
                    $"Host=localhost;Database=learnstack;Username={role};Password=s3cret", // leakwatch:ignore
            })
            .Build();

        var register = () => services.AddLearnStackPersistence(configuration);

        register.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Username='{role}'*");
    }

    [Fact]
    public void A_valid_credential_registers_without_connecting()
    {
        // The other side of the same coin: eager VALIDATION, still lazy BUILD.
        // Registration must not open a socket — nothing is listening during
        // composition — and it must not reject the credential the platform runs on.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = Valid,
            })
            .Build();

        var register = () => services.AddLearnStackPersistence(configuration);

        register.Should().NotThrow();
    }

    [Fact]
    public void An_absent_key_still_fails_lazily_rather_than_at_registration()
    {
        // Deliberate, and the reason the eager check is guarded on presence: a
        // deployment that serves only platform hosts — answered from
        // Tenancy:PlatformHosts, never from the database — legitimately has no
        // application credential, and must still boot. It fails on the first
        // request that needs a tenant, which is the first moment the absence
        // actually matters.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var register = () => services.AddLearnStackPersistence(configuration);

        register.Should().NotThrow();
    }
}
