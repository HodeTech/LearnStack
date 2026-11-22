using System.Buffers;
using System.Text.Json;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// Writes one export record into a section through a short-lived pooled buffer that is overwritten with zeros
/// afterwards, so a participant never leaves a copy of the subject's data in a buffer it does not own.
/// </summary>
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
            // ArrayBufferWriter.Clear overwrites the written span; nothing is written beyond it.
            buffer.Clear();
        }
    }
}
