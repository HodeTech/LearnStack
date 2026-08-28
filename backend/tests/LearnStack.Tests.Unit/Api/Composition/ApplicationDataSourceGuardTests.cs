using FluentAssertions;
using LearnStack.Api.Composition;
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

        foreach (var value in new[]
        {
            "Host=localhost;Username=learnstack_platform;Password=hunter2", // leakwatch:ignore
            "Host=localhost;Port=nope;Username=learnstack_app;Password=hunter2", // leakwatch:ignore
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

        messages.Should().HaveCount(2, "both values are refused");
        messages.Should().OnlyContain(message => !message.Contains("hunter2", StringComparison.Ordinal));
        messages.Should().OnlyContain(message => message.Contains("Password=***", StringComparison.Ordinal));
    }
}
