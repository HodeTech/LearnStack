using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Secrets;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.DataProtection;

/// <summary>ADR-0107 § 4: AES-256-GCM export custody, its key ring and its associated-data binding.</summary>
public sealed class DataExportCipherTests
{
    private static readonly Guid Tenant = Guid.Parse("01970000-0000-7000-8000-000000000001");
    private static readonly Guid Request = Guid.Parse("01970000-0000-7000-8000-000000000002");
    private static readonly Guid Artifact = Guid.Parse("01970000-0000-7000-8000-000000000003");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_bounded_key_ring_seals_under_its_active_key_and_opens_what_it_sealed(int count)
    {
        using var cipher = Cipher("k" + count, Enumerable.Range(1, count).ToDictionary(index => "k" + index, Key));
        var plaintext = Plaintext(1000);

        var sealedChunk = cipher.Seal(Binding(0, 3), plaintext);

        sealedChunk.KeyVersion.Should().Be("k" + count);
        cipher.ActiveKeyVersion.Should().Be("k" + count);
        sealedChunk.Nonce.Should().HaveCount(DataRequestLimits.NonceBytes);
        sealedChunk.Tag.Should().HaveCount(DataRequestLimits.TagBytes);
        sealedChunk.Ciphertext.Should().HaveCount(plaintext.Length).And.NotEqual(plaintext);
        cipher.Open(Binding(0, 3), sealedChunk).Should().Equal(plaintext);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("not_json")]
    [InlineData("not_object")]
    [InlineData("extra_property")]
    [InlineData("no_active")]
    [InlineData("active_unknown")]
    [InlineData("active_not_string")]
    [InlineData("no_keys")]
    [InlineData("empty_keys")]
    [InlineData("five_keys")]
    [InlineData("short_key")]
    [InlineData("long_key")]
    [InlineData("not_base64")]
    [InlineData("duplicate_material")]
    [InlineData("bad_label")]
    [InlineData("long_label")]
    [InlineData("key_not_string")]
    public void A_missing_or_malformed_key_ring_refuses_construction_without_detail(string ring)
    {
        var secret = ring switch
        {
            "absent" => null,
            "not_json" => "{",
            "not_object" => "[]",
            "extra_property" => Ring("k1", new() { ["k1"] = Key(1) }, extra: true),
            "no_active" => JsonSerializer.Serialize(new { keys = new Dictionary<string, string> { ["k1"] = Convert.ToBase64String(Key(1)) } }),
            "active_unknown" => Ring("k9", new() { ["k1"] = Key(1) }),
            "active_not_string" => "{\"active\":1,\"keys\":{\"k1\":\"" + Convert.ToBase64String(Key(1)) + "\"}}",
            "no_keys" => "{\"active\":\"k1\",\"other\":{}}",
            "empty_keys" => "{\"active\":\"k1\",\"keys\":{}}",
            "five_keys" => Ring("k1", Enumerable.Range(1, 5).ToDictionary(index => "k" + index, Key)),
            "short_key" => Ring("k1", new() { ["k1"] = new byte[31] }),
            "long_key" => Ring("k1", new() { ["k1"] = new byte[33] }),
            "not_base64" => "{\"active\":\"k1\",\"keys\":{\"k1\":\"%%%\"}}",
            "duplicate_material" => Ring("k1", new() { ["k1"] = Key(1), ["k2"] = Key(1) }),
            "bad_label" => Ring("k 1", new() { ["k 1"] = Key(1) }),
            "long_label" => Ring(new string('k', 33), new() { [new string('k', 33)] = Key(1) }),
            "key_not_string" => "{\"active\":\"k1\",\"keys\":{\"k1\":5}}",
            _ => throw new ArgumentOutOfRangeException(nameof(ring)),
        };

        var act = () => new DataExportCipher(new FixedSecrets(secret));

        // The refusal names the setting and its environment spelling so an operator knows what to supply, and nothing
        // of the value: no label, no key, not which rule it broke.
        var refused = act.Should().Throw<InvalidOperationException>()
            .WithMessage("Data export custody unavailable: the setting DataExport:KeyRing (environment DataExport__KeyRing) is absent or malformed.*")
            .Which;
        refused.InnerException.Should().BeNull();
        if (secret is not null)
        {
            foreach (var fragment in new[] { "k1", "k9", "%%%", Convert.ToBase64String(Key(1)), "legacy", "other" })
                refused.Message.Should().NotContain(fragment);
        }
    }

    [Fact]
    public void The_ring_answers_whether_it_holds_a_label_and_a_disposed_ring_holds_none()
    {
        var cipher = Cipher("k2", new() { ["k1"] = Key(1), ["k2"] = Key(2) });
        cipher.HoldsKey("k1").Should().BeTrue();
        cipher.HoldsKey("k2").Should().BeTrue();
        cipher.HoldsKey("k3").Should().BeFalse();
        cipher.HoldsKey(null).Should().BeFalse();
        cipher.Dispose();
        cipher.HoldsKey("k2").Should().BeFalse("a cleared ring can open nothing");
    }

    /// <summary>
    /// Seal racing host shutdown: every call either refuses as disposed or seals under the real key, so whatever it
    /// returned opens under an intact copy of the ring. Without the gate a seal could copy a key mid-way through its
    /// zeroing and produce a chunk no intact ring opens.
    /// </summary>
    [Fact]
    public async Task A_seal_racing_disposal_either_refuses_or_seals_under_the_real_key()
    {
        using var verifier = Cipher("k1", new() { ["k1"] = Key(1) });
        var plaintext = Plaintext(4096);
        for (var round = 0; round < 50; round++)
        {
            var cipher = Cipher("k1", new() { ["k1"] = Key(1) });
            using var start = new ManualResetEventSlim();
            var sealers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                var sealedChunks = new List<DataExportSealedChunk>();
                start.Wait();
                while (true)
                {
                    try
                    {
                        sealedChunks.Add(cipher.Seal(Binding(0, 1), plaintext));
                    }
                    catch (ObjectDisposedException)
                    {
                        return sealedChunks;
                    }
                }
            })).ToArray();
            start.Set();
            await Task.Delay(1);
            cipher.Dispose();
            foreach (var sealedChunk in (await Task.WhenAll(sealers)).SelectMany(chunks => chunks))
                verifier.Open(Binding(0, 1), sealedChunk).Should().Equal(plaintext, "nothing is ever sealed under a zeroed key");
        }
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("request")]
    [InlineData("artifact")]
    [InlineData("index")]
    [InlineData("count")]
    public void Every_associated_data_member_binds_the_chunk(string member)
    {
        using var cipher = Cipher("k1", new() { ["k1"] = Key(1) });
        var sealedChunk = cipher.Seal(Binding(1, 4), Plaintext(64));
        var other = Guid.Parse("01970000-0000-7000-8000-0000000000ff");
        var moved = member switch
        {
            "tenant" => new DataExportChunkBinding(other, Request, Artifact, 1, 4),
            "request" => new DataExportChunkBinding(Tenant, other, Artifact, 1, 4),
            "artifact" => new DataExportChunkBinding(Tenant, Request, other, 1, 4),
            "index" => new DataExportChunkBinding(Tenant, Request, Artifact, 2, 4),
            "count" => new DataExportChunkBinding(Tenant, Request, Artifact, 1, 3),
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };

        FluentActions.Invoking(() => cipher.Open(moved, sealedChunk)).Should().Throw<InvalidOperationException>()
            .WithMessage("Data export custody unavailable.");
        cipher.Open(Binding(1, 4), sealedChunk).Should().Equal(Plaintext(64));
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("nonce")]
    [InlineData("short_tag")]
    [InlineData("short_nonce")]
    [InlineData("empty_ciphertext")]
    [InlineData("unknown_version")]
    [InlineData("relabelled_version")]
    public void A_tampered_truncated_or_relabelled_chunk_fails_closed(string tamper)
    {
        using var cipher = Cipher("k2", new() { ["k1"] = Key(1), ["k2"] = Key(2) });
        var sealedChunk = cipher.Seal(Binding(0, 1), Plaintext(128));
        var forged = tamper switch
        {
            "ciphertext" => sealedChunk with { Ciphertext = Flip(sealedChunk.Ciphertext) },
            "tag" => sealedChunk with { Tag = Flip(sealedChunk.Tag) },
            "nonce" => sealedChunk with { Nonce = Flip(sealedChunk.Nonce) },
            "short_tag" => sealedChunk with { Tag = sealedChunk.Tag[..12] },
            "short_nonce" => sealedChunk with { Nonce = sealedChunk.Nonce[..8] },
            "empty_ciphertext" => sealedChunk with { Ciphertext = [] },
            "unknown_version" => sealedChunk with { KeyVersion = "k9" },
            "relabelled_version" => sealedChunk with { KeyVersion = "k1" },
            _ => throw new ArgumentOutOfRangeException(nameof(tamper)),
        };

        FluentActions.Invoking(() => cipher.Open(Binding(0, 1), forged)).Should().Throw<InvalidOperationException>()
            .WithMessage("Data export custody unavailable.");
    }

    [Fact]
    public void Rotation_opens_older_chunks_while_the_old_key_stays_and_not_after_it_leaves()
    {
        byte[] plaintext = Plaintext(300);
        DataExportSealedChunk old;
        using (var before = Cipher("k1", new() { ["k1"] = Key(1) }))
        {
            old = before.Seal(Binding(0, 1), plaintext);
        }

        using var rotated = Cipher("k2", new() { ["k1"] = Key(1), ["k2"] = Key(2) });
        rotated.Open(Binding(0, 1), old).Should().Equal(plaintext);
        rotated.Seal(Binding(0, 1), plaintext).KeyVersion.Should().Be("k2");

        using var retired = Cipher("k2", new() { ["k2"] = Key(2) });
        FluentActions.Invoking(() => retired.Open(Binding(0, 1), old)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Every_chunk_gets_a_fresh_nonce()
    {
        using var cipher = Cipher("k1", new() { ["k1"] = Key(1) });
        var nonces = Enumerable.Range(0, DataRequestLimits.MaximumChunkCount)
            .Select(index => Convert.ToHexString(cipher.Seal(Binding(index, DataRequestLimits.MaximumChunkCount), Plaintext(16)).Nonce))
            .ToArray();

        nonces.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(DataRequestLimits.ChunkPlaintextBytes, true)]
    [InlineData(DataRequestLimits.ChunkPlaintextBytes + 1, false)]
    public void A_chunk_holds_one_to_at_most_one_hundred_twenty_eight_kibibytes(int length, bool admitted)
    {
        using var cipher = Cipher("k1", new() { ["k1"] = Key(1) });

        var seal = () => cipher.Seal(Binding(0, 1), new byte[length]);

        if (admitted)
        {
            cipher.Open(Binding(0, 1), seal()).Should().HaveCount(length);
        }
        else
        {
            seal.Should().Throw<InvalidOperationException>();
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, DataRequestLimits.MaximumChunkCount + 1)]
    public void A_binding_names_a_position_inside_its_artifact(int index, int count)
    {
        FluentActions.Invoking(() => new DataExportChunkBinding(Tenant, Request, Artifact, index, count))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new DataExportChunkBinding(Guid.Empty, Request, Artifact, 0, 1))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Nothing_prints_key_material_or_content_and_a_disposed_cipher_refuses()
    {
        var cipher = Cipher("k1", new() { ["k1"] = Key(1) });
        var sealedChunk = cipher.Seal(Binding(0, 1), Plaintext(32));

        cipher.ToString().Should().Be(nameof(DataExportCipher));
        sealedChunk.ToString().Should().Be(nameof(DataExportSealedChunk));
        Binding(0, 1).ToString().Should().Be(nameof(DataExportChunkBinding));
        cipher.Dispose();
        FluentActions.Invoking(() => cipher.Seal(Binding(0, 1), Plaintext(32))).Should().Throw<ObjectDisposedException>();
        FluentActions.Invoking(() => cipher.Open(Binding(0, 1), sealedChunk)).Should().Throw<ObjectDisposedException>();
    }

    private static DataExportChunkBinding Binding(int index, int count) => new(Tenant, Request, Artifact, index, count);

    private static DataExportCipher Cipher(string active, Dictionary<string, byte[]> keys) => new(new FixedSecrets(Ring(active, keys)));

    private static string Ring(string active, Dictionary<string, byte[]> keys, bool extra = false)
    {
        var encoded = keys.ToDictionary(pair => pair.Key, pair => Convert.ToBase64String(pair.Value));
        return extra
            ? JsonSerializer.Serialize(new { active, keys = encoded, legacy = true })
            : JsonSerializer.Serialize(new { active, keys = encoded });
    }

    private static byte[] Key(int seed) => Enumerable.Repeat((byte)seed, DataRequestLimits.KeyBytes).ToArray();

    private static byte[] Plaintext(int length) => Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

    private static byte[] Flip(byte[] value)
    {
        var copy = value.ToArray();
        copy[0] ^= 0x01;
        return copy;
    }

    private sealed class FixedSecrets(string? ring) : ISecretProvider
    {
        public string? GetSecret(string key) => key == DataExportCipher.KeyRingSecret ? ring : null;
    }
}
