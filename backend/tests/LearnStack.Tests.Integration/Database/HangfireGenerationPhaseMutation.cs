using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Mutates one call instruction in a disposable child-only copy. The test host,
/// production output and original vendor assemblies are never changed or loaded.
/// </summary>
internal sealed class HangfireGenerationPhaseMutation(string directory, bool ownsDirectory) : IDisposable
{
    private const string Infrastructure = "LearnStack.Infrastructure.dll";
    private const string Generation = "LearnStack.Infrastructure.Jobs.HangfireGeneration";
    private const string Gate = "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate";
    private static readonly byte[] VoidStringSignature = [0x00, 0x01, 0x01, 0x0e];
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .DistinctBy(opcode => opcode.Value).ToDictionary(opcode => opcode.Value);

    internal string Directory { get; } = directory;

    internal static async Task<HangfireGenerationPhaseMutation> CreateAsync(string source, string mutation)
    {
        if (mutation == "none") return new(source, ownsDirectory: false);
        Require(mutation is "sink" or "constructor" or "capture_dispose" or "second_announce" or "outer_completion");
        var target = System.IO.Directory.CreateTempSubdirectory("learnstack-phase-mutant-").FullName;
        var result = new HangfireGenerationPhaseMutation(target, ownsDirectory: true);
        try
        {
            var paths = System.IO.Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            Require(paths.Length is > 0 and < 1000);
            foreach (var sourceFile in paths)
            {
                Require(new FileInfo(sourceFile).LinkTarget is null);
                var destination = Path.Combine(target, Path.GetRelativePath(source, sourceFile));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(sourceFile, destination);
            }
            // This tree contains only staged binaries, never the stdin secret.
            // CreateTempSubdirectory can choose 0700; the container's distinct
            // nonroot UID must be able to read its read-only bind mount.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                const UnixFileMode readableDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
                const UnixFileMode readableFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                File.SetUnixFileMode(target, readableDirectory);
                foreach (var childDirectory in System.IO.Directory.GetDirectories(target, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(childDirectory, readableDirectory);
                foreach (var file in System.IO.Directory.GetFiles(target, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(file, readableFile);
            }
            var original = await File.ReadAllBytesAsync(Path.Combine(source, Infrastructure));
            var patched = Patch(original, mutation);
            await File.WriteAllBytesAsync(Path.Combine(target, Infrastructure), patched);
            foreach (var sourceFile in paths)
            {
                var relative = Path.GetRelativePath(source, sourceFile);
                var copied = await File.ReadAllBytesAsync(Path.Combine(target, relative));
                var currentSource = await File.ReadAllBytesAsync(sourceFile);
                Require(relative == Infrastructure
                    ? copied.SequenceEqual(patched) && currentSource.SequenceEqual(original)
                    : SHA256.HashData(copied).SequenceEqual(SHA256.HashData(currentSource)));
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static byte[] Patch(byte[] original, string mutation)
    {
        using var stream = new MemoryStream(original, writable: false);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var methods = metadata.TypeDefinitions.SelectMany(typeHandle =>
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = TypeName(metadata, typeHandle);
            return type.GetMethods().Select(methodHandle => (Type: typeName, Handle: methodHandle,
                Method: metadata.GetMethodDefinition(methodHandle)));
        }).ToArray();
        if (mutation == "sink")
        {
            var sinks = 0;
            foreach (var candidate in methods.Where(candidate => candidate.Method.RelativeVirtualAddress > 0))
            {
                var candidateIl = pe.GetMethodBody(candidate.Method.RelativeVirtualAddress).GetILBytes() ?? throw Refused();
                foreach (var instruction in Instructions(candidateIl).Where(instruction => instruction.Opcode == OpCodes.Call
                    || instruction.Opcode == OpCodes.Callvirt))
                {
                    var handle = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(candidateIl.AsSpan(instruction.Offset + 1, 4)));
                    var called = MethodIdentity(metadata, handle);
                    if (called.Type == "System.Environment" && called.Name == "FailFast") sinks++;
                }
            }
            Require(sinks == 1);
        }
        var candidates = methods.Where(candidate => mutation switch
        {
            "sink" => candidate.Type == Gate && metadata.GetString(candidate.Method.Name) == "FailFast",
            "constructor" => candidate.Type == Generation && metadata.GetString(candidate.Method.Name) == "Construct",
            "capture_dispose" => candidate.Type == Generation + "+CaptureDispatcher" && metadata.GetString(candidate.Method.Name) == "Dispose",
            "second_announce" => candidate.Type == Generation && metadata.GetString(candidate.Method.Name) == "AnnounceServer",
            "outer_completion" => candidate.Type.StartsWith(Generation + "+<ObserveCompletionAsync>d__", StringComparison.Ordinal)
                && metadata.GetString(candidate.Method.Name) == "MoveNext",
            _ => false,
        }).ToArray();
        Require(candidates.Length == 1 && candidates[0].Method.RelativeVirtualAddress > 0);
        var method = candidates[0].Method;
        var body = pe.GetMethodBody(method.RelativeVirtualAddress);
        var il = body.GetILBytes() ?? throw Refused();
        var calls = Instructions(il).Where(instruction => instruction.Opcode == OpCodes.Call
            || instruction.Opcode == OpCodes.Callvirt).Where(instruction =>
        {
            var handle = MetadataTokens.EntityHandle(BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(instruction.Offset + 1, 4)));
            var target = MethodIdentity(metadata, handle);
            return mutation switch
            {
                "sink" => target.Type == "System.Environment" && target.Name == "FailFast"
                    && target.Signature.SequenceEqual(VoidStringSignature),
                "constructor" => target.Type == Gate + "+PoolBoundary" && target.Name == "Terminate",
                _ => target.Type == Generation && target.Name == "ObserveUnwind",
            };
        }).ToArray();
        Require(calls.Length == 1);
        var section = pe.PEHeaders.SectionHeaders.Single(section => method.RelativeVirtualAddress >= section.VirtualAddress
            && method.RelativeVirtualAddress < section.VirtualAddress + section.SizeOfRawData);
        var bodyOffset = section.PointerToRawData + method.RelativeVirtualAddress - section.VirtualAddress;
        var headerSize = (original[bodyOffset] & 3) == 2 ? 1
            : (BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(bodyOffset, 2)) >> 12) * 4;
        Require(headerSize >= 1 && original.AsSpan(bodyOffset + headerSize, il.Length).SequenceEqual(il));
        var offset = bodyOffset + headerSize + calls[0].Offset;
        var replacement = mutation == "constructor"
            ? new byte[] { 0x26, 0x26, 0x00, 0x00, 0x00 }
            : new byte[] { 0x26, 0x00, 0x00, 0x00, 0x00 };
        var patched = (byte[])original.Clone();
        replacement.CopyTo(patched, offset);
        Require(!patched.SequenceEqual(original) && patched.AsSpan(0, offset).SequenceEqual(original.AsSpan(0, offset))
            && patched.AsSpan(offset + 5).SequenceEqual(original.AsSpan(offset + 5))
            && patched.AsSpan(offset, 5).SequenceEqual(replacement));
        return patched;
    }

    private static IEnumerable<(int Offset, OpCode Opcode)> Instructions(byte[] il)
    {
        for (var offset = 0; offset < il.Length;)
        {
            var start = offset;
            short value = il[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
            Require(Opcodes.TryGetValue(value, out var opcode));
            var size = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                    or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                    or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, 4))),
                _ => throw Refused(),
            };
            Require(size >= 0 && offset + size <= il.Length);
            yield return (start, opcode);
            offset += size;
        }
    }

    private static (string Type, string Name, byte[] Signature) MethodIdentity(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodDefinition)
        {
            var method = metadata.GetMethodDefinition((MethodDefinitionHandle)handle);
            return (TypeName(metadata, method.GetDeclaringType()), metadata.GetString(method.Name), metadata.GetBlobBytes(method.Signature));
        }
        if (handle.Kind == HandleKind.MemberReference)
        {
            var member = metadata.GetMemberReference((MemberReferenceHandle)handle);
            return (TypeName(metadata, member.Parent), metadata.GetString(member.Name), metadata.GetBlobBytes(member.Signature));
        }
        return (string.Empty, string.Empty, []);
    }

    private static string TypeName(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            var parent = type.GetDeclaringType();
            return parent.IsNil ? metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)
                : TypeName(metadata, parent) + "+" + metadata.GetString(type.Name);
        }
        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        }
        return string.Empty;
    }

    public void Dispose()
    {
        if (ownsDirectory && System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }

    private static void Require(bool condition) { if (!condition) throw Refused(); }
    private static InvalidOperationException Refused() => new("The closed child-only instruction mutation could not be qualified.");
}
