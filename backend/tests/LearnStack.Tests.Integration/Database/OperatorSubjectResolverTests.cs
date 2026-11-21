using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The contract of <c>fn_identity_operator_resolve_user</c> against real PostgreSQL as the application role
/// (ADR-0102 § 5, Database Standards § Operator subject resolution).
/// </summary>
/// <remarks>
/// The definer is the one place an operator act reads <c>users</c>, which a tenant announcement blanks for the
/// application role, so each refusal it promises is asserted here rather than left to the handler above it.
/// </remarks>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class OperatorSubjectResolverTests(PublicReadFixture parent)
{
    private const string Resolve = "SELECT public.fn_identity_operator_resolve_user(@authority, @key)";

    [Fact]
    public async Task An_Active_User_Is_Found_And_An_Unknown_Subject_Is_Not()
    {
        await using var proof = await Proof.CreateAsync(parent);

        (await proof.ResolveAsync(proof.Subject)).Should().Be(proof.Person.Value);
        (await proof.ResolveAsync("nobody-has-signed-in")).Should().BeNull();
    }

    [Fact]
    public async Task A_User_Who_Is_No_Longer_Active_Is_Not_Found()
    {
        await using var proof = await Proof.CreateAsync(parent);
        await proof.CloseUserAsync();

        (await proof.ResolveAsync(proof.Subject)).Should().BeNull("the Active filter is part of the contract, not a courtesy");
    }

    [Fact]
    public async Task The_Definer_Refuses_Every_Caller_And_Argument_It_Does_Not_Admit()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var key = proof.Key(proof.Subject);

        (await proof.RefusalAsync(authority: Guid.NewGuid(), key: key)).Should().Be("23514", "another authority");
        (await proof.RefusalAsync(key: key[..31])).Should().Be("23514", "a key that is not 32 bytes");
        (await proof.RefusalAsync(key: [])).Should().Be("23514", "an empty key");
        (await proof.RefusalAsync(key: key, announceTenant: false)).Should().Be("23514", "no tenant announcement");
        (await proof.RefusalAsync(key: key, readOnly: true)).Should().Be("23514", "a read-only transaction");
        (await proof.RefusalAsync(key: key, asOwner: true)).Should().Be("23514", "a session that is not the application role");
        (await proof.RefusalAsync(key: key, repeatableRead: true)).Should().Be("23514", "a transaction that is not READ COMMITTED");
        (await proof.RefusalAsync(key: key, nullAuthority: true)).Should().Be("23514", "a null authority");
        (await proof.RefusalAsync(nullKey: true)).Should().Be("23514", "a null key");
        (await proof.RefusalAsync(key: key)).Should().BeNull("the control: the same call, admitted");
    }

    private sealed class Proof : IAsyncDisposable
    {
        private readonly TenantAuthenticationIssuer _issuer;
        private readonly IdentityPublicReadFixture _database;

        private Proof(TenantAuthenticationIssuer issuer, IdentityPublicReadFixture database, UserId person, string subject)
        {
            _issuer = issuer;
            _database = database;
            Person = person;
            Subject = subject;
        }

        internal UserId Person { get; }

        internal string Subject { get; }

        internal static async Task<Proof> CreateAsync(PublicReadFixture parent)
        {
            var issuer = await TenantAuthenticationIssuer.StartAsync();
            var person = UserId.From(Guid.CreateVersion7());
            var subject = "resolver-person-" + person.Value.ToString("N");
            try
            {
                var database = await IdentityPublicReadFixture.CreateAsync(
                    parent, issuer.Issuer, new Dictionary<UserId, string> { [person] = subject });
                return new Proof(issuer, database, person, subject);
            }
            catch
            {
                await issuer.DisposeAsync();
                throw;
            }
        }

        internal byte[] Key(string subject) =>
            IdentitySubjectKey.Compute(_database.Registration.AuthorityId, subject).ToByteArray();

        /// <summary>One call as the application role under a tenant announcement, the way an act makes it.</summary>
        internal async Task<Guid?> ResolveAsync(string subject)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, Guid.CreateVersion7());
            await using var command = Command(app, transaction, _database.Registration.AuthorityId.Value, Key(subject));
            return await command.ExecuteScalarAsync() is Guid found ? found : null;
        }

        /// <summary>The SQLSTATE the definer raises for the call, or null when it answers.</summary>
        internal async Task<string?> RefusalAsync(
            Guid? authority = null, byte[]? key = null, bool announceTenant = true, bool readOnly = false, bool asOwner = false,
            bool repeatableRead = false, bool nullAuthority = false, bool nullKey = false)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(
                asOwner ? _database.MigrationConnectionString : _database.AppConnectionString);
            await using var transaction = await connection.BeginTransactionAsync(
                repeatableRead ? System.Data.IsolationLevel.RepeatableRead : System.Data.IsolationLevel.ReadCommitted);
            if (readOnly)
            {
                await IdentityMembershipFixture.ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY");
            }

            if (announceTenant)
            {
                await IdentityMembershipFixture.ScopeAsync(connection, transaction, Guid.CreateVersion7());
            }

            try
            {
                await using var command = Command(
                    connection, transaction, authority ?? _database.Registration.AuthorityId.Value, key ?? Key(Subject));
                if (nullAuthority) command.Parameters["authority"].Value = DBNull.Value;
                if (nullKey) command.Parameters["key"].Value = DBNull.Value;
                await command.ExecuteScalarAsync();
                return null;
            }
            catch (PostgresException failure)
            {
                return failure.SqlState;
            }
        }

        /// <summary>
        /// The fixture's one corruption: the user closed, with the immutability trigger and the insert check stepped
        /// around exactly as the administration-eligibility cases do, and restored before the definer is called.
        /// </summary>
        internal async Task CloseUserAsync()
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
            var check = await IdentityMembershipFixture.ScalarAsync<string>(owner, null,
                "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
            await using var transaction = await owner.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(owner, transaction, $"""
                ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
                ALTER POLICY users_isolation ON public.users WITH CHECK (({check}) OR
                    (current_user='learnstack_migration' AND id='{Person.Value:D}'::uuid AND status='Closing'
                     AND deleted_at IS NULL AND deleted_by IS NULL));
                UPDATE public.users SET status='Closing' WHERE id='{Person.Value:D}'::uuid;
                ALTER POLICY users_isolation ON public.users WITH CHECK ({check});
                ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update;
                """);
            await transaction.CommitAsync();
        }

        private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid authority, byte[] key)
        {
            var command = new NpgsqlCommand(Resolve, connection, transaction);
            command.Parameters.AddWithValue("authority", authority);
            command.Parameters.AddWithValue("key", key);
            return command;
        }

        public async ValueTask DisposeAsync()
        {
            await _database.DisposeAsync();
            await _issuer.DisposeAsync();
        }
    }
}
