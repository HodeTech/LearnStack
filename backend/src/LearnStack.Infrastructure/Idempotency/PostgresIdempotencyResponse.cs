using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Idempotency;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Idempotency;

internal sealed record PostgresIdempotencyResponse(int Status, string? Content, string Headers, byte[] Body,
    IdempotencyReplayTarget? Target)
{
    internal static PostgresIdempotencyResponse? Capture(IdempotentResponse? response)
    {
        if (response is null) return null;
        Validate(response);
        // Freeze caller-owned arrays/collections before the first asynchronous wait.
        return new(response.StatusCode, response.ContentType,
            JsonSerializer.Serialize(response.Headers), response.Body.ToArray(), response.ReplayTarget);
    }

    internal static IdempotentResponse Read(NpgsqlDataReader reader)
    {
        try
        {
            var headers = reader.IsDBNull(5) ? new Dictionary<string, string[]>()
                : JsonSerializer.Deserialize<Dictionary<string, string[]>>(reader.GetString(5))
                    ?? throw new IdempotencyStoreException();
            IdempotencyReplayTarget? target = null;
            if (!reader.IsDBNull(7) || !reader.IsDBNull(8))
            {
                if (reader.IsDBNull(7) || reader.IsDBNull(8)
                    || !Enum.TryParse<IdempotencyReplayTargetKind>(reader.GetString(7), out var kind)
                    || !Enum.IsDefined(kind)) throw new IdempotencyStoreException();
                target = new(kind, reader.GetGuid(8));
            }
            var response = new IdempotentResponse(reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                headers.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value, StringComparer.OrdinalIgnoreCase),
                reader.GetFieldValue<byte[]>(6), target);
            Validate(response);
            return response;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidCastException)
        {
            throw new IdempotencyStoreException();
        }
    }

    private static void Validate(IdempotentResponse response)
    {
        if (response.StatusCode is < 100 or > 599 || response.Body is null || response.Headers is null
            || response.ContentType is { } content && !JsonValue.IsStorableText(content)) Invalid();
        long size = response.Body!.LongLength;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers!)
        {
            if (string.IsNullOrEmpty(name) || !JsonValue.IsStorableText(name) || !names.Add(name) || values is null) Invalid();
            size += Encoding.UTF8.GetByteCount(name);
            foreach (var value in values!)
            {
                if (value is null || !JsonValue.IsStorableText(value)) Invalid();
                size += Encoding.UTF8.GetByteCount(value!);
            }
        }
        if (response.ContentType is { } type && !response.Headers.Keys.Any(key => key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
            size += "Content-Type".Length + Encoding.UTF8.GetByteCount(type);
        if (response.ReplayTarget is { } target)
        {
            if (!Enum.IsDefined(target.Kind) || target.Id == Guid.Empty || response.StatusCode is < 200 or > 299
                || size > PostgresIdempotencyStore.MaxProtectedResponseBytes) Invalid();
        }
        else if (size > PostgresIdempotencyStore.MaxStoredResponseBytes) Invalid();
    }

    [DoesNotReturn]
    private static void Invalid() => throw new ArgumentException("Idempotency response is invalid.");

    internal static void AddParameters(NpgsqlCommand command, PostgresIdempotencyResponse? response)
    {
        command.Parameters.AddWithValue("state", response is null ? "unreplayable" : "completed");
        command.Parameters.AddWithValue("status", NpgsqlDbType.Integer, (object?)response?.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("content", NpgsqlDbType.Text, (object?)response?.Content ?? DBNull.Value);
        command.Parameters.AddWithValue("headers", NpgsqlDbType.Jsonb, (object?)response?.Headers ?? DBNull.Value);
        command.Parameters.AddWithValue("body", NpgsqlDbType.Bytea, (object?)response?.Body ?? DBNull.Value);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Text, (object?)response?.Target?.Kind.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("target", NpgsqlDbType.Uuid, (object?)response?.Target?.Id ?? DBNull.Value);
    }
}
