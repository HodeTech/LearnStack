using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Email;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Secrets;
using Xunit;

namespace LearnStack.Tests.Unit;

public sealed class InvitationEmailCipherTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Bounded_Keyring_RoundTrips_With_Its_Active_Key(int count)
    {
        var keys = Enumerable.Range(1, count).ToDictionary(index => "key" + index, index => Key(index));
        var cipher = Cipher("key" + count, keys);

        var envelope = cipher.Encrypt(Binding(), Message());

        envelope.KeyVersion.Should().Be("key" + count);
        envelope.Nonce.Should().HaveCount(12);
        envelope.Tag.Should().HaveCount(16);
        cipher.Decrypt(Binding(), envelope).Should().Be(Message());
    }

    [Theory]
    [MemberData(nameof(InvalidKeyRings))]
    public void Missing_Malformed_And_Duplicate_Keyrings_Fail_Closed(string? ring)
    {
        var act = () => new InvitationEmailCipher(new FixedSecrets(ring));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Invitation email custody unavailable.")
            .Which.InnerException.Should().BeNull();
    }

    public static IEnumerable<object?[]> InvalidKeyRings()
    {
        yield return [null];
        yield return ["not-json-secret-canary"];
        yield return ["{}"];
        yield return ["[]"];
        yield return ["{\"active\":\"key1\"}"];
        yield return ["{\"keys\":{}}"];
        yield return [Ring("key1", new Dictionary<string, string>())];
        yield return [Ring("missing", new Dictionary<string, string> { ["key1"] = Key(1) })];
        yield return [Ring("key1", new Dictionary<string, string> { ["key1"] = "invalid-base64-canary" })];
        yield return [Ring("key1", new Dictionary<string, string> { ["key1"] = Convert.ToBase64String(new byte[31]) })];
        yield return [Ring("key1", new Dictionary<string, string> { ["key1"] = Convert.ToBase64String(new byte[33]) })];
        yield return [Ring("", new Dictionary<string, string> { [""] = Key(1) })];
        yield return [Ring("invalid.version", new Dictionary<string, string> { ["invalid.version"] = Key(1) })];
        yield return [Ring(new string('x', 33), new Dictionary<string, string> { [new string('x', 33)] = Key(1) })];
        yield return [Ring("key1", Enumerable.Range(1, 5).ToDictionary(index => "key" + index, index => Key(index)))];
        yield return [Ring("key1", new Dictionary<string, string> { ["key1"] = Key(1), ["key2"] = Key(1) })];
        yield return ["{\"active\":\"key1\",\"keys\":{\"key1\":\"" + Key(1) + "\",\"key1\":\"" + Key(2) + "\"}}"];
    }

    [Fact]
    public void Plaintext_ByteBudget_Is_Inclusive_At_32KiB_And_Refuses_One_More_Byte()
    {
        var cipher = Cipher();
        var empty = Message() with { TextBody = string.Empty };
        var overhead = JsonSerializer.SerializeToUtf8Bytes(empty).Length;
        var exact = empty with { TextBody = new string('x', InvitationEmailCipher.MaximumBytes - overhead) };
        JsonSerializer.SerializeToUtf8Bytes(exact).Should().HaveCount(32 * 1024);

        var envelope = cipher.Encrypt(Binding(), exact);

        envelope.Ciphertext.Should().HaveCount(32 * 1024);
        cipher.Decrypt(Binding(), envelope).Should().Be(exact);
        var oversized = () => cipher.Encrypt(Binding(), exact with { TextBody = exact.TextBody + "x" });
        oversized.Should().Throw<InvalidOperationException>().WithMessage("Invitation email custody unavailable.");
        var oversizedStored = () => cipher.Decrypt(Binding(), envelope with { Ciphertext = new byte[32 * 1024 + 1] });
        oversizedStored.Should().Throw<InvalidOperationException>().WithMessage("Invitation email custody unavailable.");
    }

    [Theory]
    [InlineData("spool")]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("organization_absent")]
    [InlineData("invitation")]
    [InlineData("generation")]
    [InlineData("job")]
    public void Every_Bound_Scope_And_Identifier_Rejects_Ciphertext_Reassignment(string changed)
    {
        var cipher = Cipher();
        var binding = Binding();
        var envelope = cipher.Encrypt(binding, Message());
        var replacement = Guid.Parse("019712ac-1234-7000-8000-000000000099");
        var altered = changed switch
        {
            "spool" => binding with { SpoolId = replacement },
            "tenant" => binding with { TenantId = replacement },
            "organization" => binding with { OrganizationId = replacement },
            "organization_absent" => binding with { OrganizationId = null },
            "invitation" => binding with { InvitationId = replacement },
            "generation" => binding with { GenerationId = replacement },
            "job" => binding with { JobId = replacement },
            _ => throw new ArgumentOutOfRangeException(nameof(changed)),
        };

        var act = () => cipher.Decrypt(altered, envelope);

        act.Should().Throw<InvalidOperationException>().WithMessage("Invitation email custody unavailable.")
            .Which.InnerException.Should().BeNull();
    }

    [Fact]
    public void TenantWide_Ciphertext_Cannot_Be_Reassigned_To_Organization_Scope()
    {
        var cipher = Cipher();
        var tenantWide = Binding() with { OrganizationId = null };
        var envelope = cipher.Encrypt(tenantWide, Message());

        var act = () => cipher.Decrypt(Binding(), envelope);

        act.Should().Throw<InvalidOperationException>().WithMessage("Invitation email custody unavailable.");
    }

    [Fact]
    public void KeyVersion_Is_Authenticated_Even_When_The_New_Version_Has_Identical_Key_Bytes()
    {
        var envelope = Cipher().Encrypt(Binding(), Message());
        // Separate valid singleton rings isolate AAD from wrong-key decryption.
        var renamed = Cipher("key2", new Dictionary<string, string> { ["key2"] = Key(1) });

        var act = () => renamed.Decrypt(Binding(), envelope with { KeyVersion = "key2" });

        act.Should().Throw<InvalidOperationException>().WithMessage("Invitation email custody unavailable.");
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    public void Ciphertext_And_Authentication_Material_Tampering_Fails_Closed(string changed)
    {
        var cipher = Cipher();
        var envelope = cipher.Encrypt(Binding(), Message());
        static byte[] Flip(byte[] original)
        {
            var result = (byte[])original.Clone();
            result[0] ^= 1;
            return result;
        }

        var altered = changed switch
        {
            "nonce" => envelope with { Nonce = Flip(envelope.Nonce) },
            "ciphertext" => envelope with { Ciphertext = Flip(envelope.Ciphertext) },
            "tag" => envelope with { Tag = Flip(envelope.Tag) },
            _ => throw new ArgumentOutOfRangeException(nameof(changed)),
        };

        var act = () => cipher.Decrypt(Binding(), altered);

        act.Should().Throw<InvalidOperationException>().WithMessage("Invitation email custody unavailable.");
    }

    [Fact]
    public void Active_Key_Rotation_Preserves_Old_Ciphertext_Decryption()
    {
        var old = Cipher().Encrypt(Binding(), Message());
        var rotated = Cipher("key2", new Dictionary<string, string> { ["key1"] = Key(1), ["key2"] = Key(2) });

        rotated.Decrypt(Binding(), old).Should().Be(Message());
        var current = rotated.Encrypt(Binding(), Message());

        current.KeyVersion.Should().Be("key2");
        rotated.Decrypt(Binding(), current).Should().Be(Message());
    }

    [Fact]
    public void Missing_Old_Key_Refuses_Without_Fallback_And_Restored_Key_Recovers_Original_Message()
    {
        var envelope = Cipher().Encrypt(Binding(), Message());
        var missing = Cipher("key2", new Dictionary<string, string> { ["key2"] = Key(2) });

        var act = () => missing.Decrypt(Binding(), envelope);

        act.Should().Throw<InvalidOperationException>().WithMessage("Invitation email custody unavailable.");
        var restored = Cipher("key2", new Dictionary<string, string> { ["key1"] = Key(1), ["key2"] = Key(2) });
        restored.Decrypt(Binding(), envelope).Should().Be(Message());
    }

    [Fact]
    public void Ciphertext_And_Message_String_Representations_Reveal_No_Content()
    {
        var message = Message();
        var envelope = Cipher().Encrypt(Binding(), message);

        message.ToString().Should().Be(nameof(EmailSubmission));
        envelope.ToString().Should().Be(nameof(InvitationEmailCiphertext));
    }

    private static InvitationEmailCipher Cipher(string active = "key1", Dictionary<string, string>? keys = null) =>
        new(new FixedSecrets(Ring(active, keys ?? new Dictionary<string, string> { ["key1"] = Key(1) })));

    private static string Ring(string active, Dictionary<string, string> keys) => JsonSerializer.Serialize(new { active, keys });
    private static string Key(int value) => Convert.ToBase64String(Enumerable.Repeat((byte)value, 32).ToArray());
    private static EmailSubmission Message() => new("recipient-canary@example.test", "subject-canary",
        "text-canary", "<p>html-canary</p>", "message-canary@learnstack.invalid");

    private static InvitationEmailBinding Binding() => new(
        Guid.Parse("019712ac-1234-7000-8000-000000000001"),
        Guid.Parse("019712ac-1234-7000-8000-000000000002"),
        Guid.Parse("019712ac-1234-7000-8000-000000000003"),
        Guid.Parse("019712ac-1234-7000-8000-000000000004"),
        Guid.Parse("019712ac-1234-7000-8000-000000000005"),
        Guid.Parse("019712ac-1234-7000-8000-000000000006"));

    private sealed class FixedSecrets(string? ring) : ISecretProvider
    {
        public string? GetSecret(string key) => key == "InvitationEmail:KeyRing" ? ring : null;
    }
}
