using System.Text;
using Google.Protobuf;

namespace LearnStack.Tests.Integration;

internal sealed partial class TenantAuthenticationDiagnosticCollector
{
    // Decode the actual OTLP wire payload. In particular, server.port is an
    // integer AnyValue; searching UTF-8 payload text for decimal digits cannot
    // establish whether that destination dimension was exported.
    internal DiagnosticMetricPoint[] MetricPoints => _requests
        .Where(request => request.Path == "/opentelemetry.proto.collector.metrics.v1.MetricsService/Export")
        .SelectMany(request => ReadMetricPoints(request.Data)).ToArray();

    private static IEnumerable<DiagnosticMetricPoint> ReadMetricPoints(byte[] payload)
    {
        foreach (var resource in Nested(payload, 1))
            foreach (var scope in Nested(resource, 2))
            {
                var scopeName = Nested(scope, 1).Select(item => Text(item, 1)).SingleOrDefault() ?? "";
                foreach (var metric in Nested(scope, 2))
                {
                    var name = Text(metric, 1);
                    foreach (var data in Fields(metric).Where(field => field.Number is 5 or 7 or 9 or 10 or 11))
                        foreach (var point in Nested(data.Bytes
                            ?? throw new InvalidDataException("OTLP metric data must be a message."), 1))
                        {
                            var attributeField = data.Number switch { 9 => 9, 10 => 1, _ => 7 };
                            var attributes = ReadAttributes(point, attributeField);
                            var fields = Fields(point);
                            var value = data.Number is 5 or 7
                                ? ReadNumber(fields)
                                : fields.Single(field => field.Number == 4).Scalar;
                            var exemplarField = data.Number switch { 9 => 8, 10 => 11, 5 or 7 => 5, _ => 0 };
                            var exemplars = Nested(point, exemplarField).Select(exemplar => new DiagnosticMetricExemplar(
                                ReadAttributes(exemplar, 7),
                                ReadNumber(Fields(exemplar), doubleField: 3),
                                Convert.ToHexString(Nested(exemplar, 5).SingleOrDefault() ?? []),
                                Convert.ToHexString(Nested(exemplar, 4).SingleOrDefault() ?? []))).ToArray();
                            yield return new DiagnosticMetricPoint(scopeName, name, attributes, value, exemplars);
                        }
                }
            }
    }

    private static Dictionary<string, object> ReadAttributes(byte[] payload, int number) =>
        Nested(payload, number).ToDictionary(
            attribute => Text(attribute, 1),
            attribute => ReadAttribute(Nested(attribute, 2).Single()), StringComparer.Ordinal);

    private static double ReadNumber(WireField[] fields, int doubleField = 4) =>
        fields.Single(field => field.Number == doubleField || field.Number == 6) switch
        {
            { Number: 6 } integer => unchecked((long)integer.Scalar),
            var number => BitConverter.Int64BitsToDouble(unchecked((long)number.Scalar)),
        };

    private static object ReadAttribute(byte[] payload) => Fields(payload).Single() switch
    {
        { Number: 1, Bytes: { } value } => Encoding.UTF8.GetString(value),
        { Number: 2 } value => value.Scalar != 0,
        { Number: 3 } value => unchecked((long)value.Scalar),
        { Number: 4 } value => BitConverter.Int64BitsToDouble(unchecked((long)value.Scalar)),
        _ => throw new InvalidDataException("Unexpected nonscalar OTLP metric attribute."),
    };

    private static string Text(byte[] payload, int number) =>
        Encoding.UTF8.GetString(Nested(payload, number).Single());

    private static IEnumerable<byte[]> Nested(byte[] payload, int number) => Fields(payload)
        .Where(field => field.Number == number).Select(field => field.Bytes).OfType<byte[]>();

    private static WireField[] Fields(byte[] payload)
    {
        using var input = new CodedInputStream(payload);
        var fields = new List<WireField>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var number = WireFormat.GetTagFieldNumber(tag);
            switch (WireFormat.GetTagWireType(tag))
            {
                case WireFormat.WireType.LengthDelimited:
                    fields.Add(new(number, input.ReadBytes().ToByteArray(), 0));
                    break;
                case WireFormat.WireType.Varint:
                    fields.Add(new(number, null, input.ReadUInt64()));
                    break;
                case WireFormat.WireType.Fixed64:
                    fields.Add(new(number, null, input.ReadFixed64()));
                    break;
                case WireFormat.WireType.Fixed32:
                    fields.Add(new(number, null, input.ReadFixed32()));
                    break;
                default:
                    throw new InvalidDataException("Unexpected OTLP metric wire type.");
            }
        }
        return fields.ToArray();
    }

    private readonly record struct WireField(int Number, byte[]? Bytes, ulong Scalar);
}

internal sealed record DiagnosticMetricPoint(
    string ScopeName, string Name, IReadOnlyDictionary<string, object> Attributes, double Value,
    DiagnosticMetricExemplar[] Exemplars);

internal sealed record DiagnosticMetricExemplar(
    IReadOnlyDictionary<string, object> FilteredAttributes, double Value, string TraceId, string SpanId);
