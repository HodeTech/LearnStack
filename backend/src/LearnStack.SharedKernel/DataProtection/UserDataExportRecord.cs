using System.Buffers;
using System.Text.Json;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// Writes one export record into a section through a short-lived buffer that is overwritten with zeros afterwards.
/// </summary>
/// <remarks>
/// Plaintext hygiene here is best effort: the buffer this method owns is cleared, but an
/// <see cref="ArrayBufferWriter{T}"/> that grows leaves its earlier, smaller arrays to the garbage collector uncleared,
/// and the JSON writer, the participant's own strings and the runtime may hold further copies. Process and crash dumps
/// of the API therefore hold personal data and are handled as such (Data Protection § Right of Access).
/// </remarks>
public static class UserDataExportRecord
{
    private static readonly JsonWriterOptions Options = new() { Indented = false, SkipValidation = false };

    /// <summary>Serializes one JSON value with <paramref name="write"/> and appends it to <paramref name="collection"/>.</summary>
    /// <exception cref="InvalidOperationException">The callback wrote no value, or more than one.</exception>
    public static async ValueTask<UserDataExportAppend> AppendAsync(IUserDataExportWriter writer, string collection,
        Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(write);
        var buffer = new ArrayBufferWriter<byte>(1024);
        try
        {
            using (var json = new Utf8JsonWriter(buffer, Options))
            {
                write(json);
                json.Flush();
                if (json.CurrentDepth != 0 || json.BytesCommitted == 0)
                {
                    throw new InvalidOperationException("An export record is exactly one complete JSON value.");
                }
            }

            return await writer.AppendAsync(collection, buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // ArrayBufferWriter.Clear overwrites the written span of its current array; nothing is written beyond it.
            buffer.Clear();
        }
    }
}
