using System.Text;

namespace CtxRelay.Sessions;

// Field numbers follow the Cortex protobuf descriptors shipped with Antigravity.
// Read only: encrypted legacy .pb files are deliberately not treated as protobuf.
static class AntigravityProto
{
    public sealed record Field(int Number, ulong Value, byte[] Data);
    public static List<Field> Fields(byte[] bytes)
    {
        var result = new List<Field>(); int position = 0;
        ulong Varint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (position >= bytes.Length) throw new InvalidDataException("Incomplete Antigravity protobuf.");
                var part = bytes[position++]; value |= (ulong)(part & 127) << shift;
                if (part < 128) return value;
            }
            throw new InvalidDataException("Invalid Antigravity protobuf varint.");
        }
        while (position < bytes.Length)
        {
            var tag = Varint(); int number = checked((int)(tag >> 3));
            if (number == 0) throw new InvalidDataException("Invalid Antigravity protobuf tag.");
            if ((tag & 7) == 0) { result.Add(new(number, Varint(), Array.Empty<byte>())); continue; }
            var length = (tag & 7) switch { 1 => 8, 5 => 4, 2 => checked((int)Varint()), _ => throw new InvalidDataException("Unsupported protobuf wire type.") };
            if (length < 0 || length > bytes.Length - position) throw new InvalidDataException("Incomplete Antigravity protobuf field.");
            result.Add(new(number, 0, bytes.AsSpan(position, length).ToArray())); position += length;
        }
        return result;
    }
    public static byte[] Blob(byte[] bytes, int number) => Fields(bytes).FirstOrDefault(f => f.Number == number)?.Data ?? Array.Empty<byte>();
    public static string Text(byte[] bytes, params int[] numbers) => string.Join("\n", Fields(bytes)
        .Where(f => numbers.Contains(f.Number)).Select(f => Encoding.UTF8.GetString(f.Data)).Where(s => s.Length > 0));
    public static string Timestamp(byte[] metadata, int field = 1)
    {
        var timestamp = Blob(metadata, field); if (timestamp.Length == 0) return "";
        var seconds = Fields(timestamp).FirstOrDefault(f => f.Number == 1)?.Value ?? 0;
        return seconds > 0 && seconds < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToString("O") : "";
    }
    public static string Workspace(byte[] metadata)
    {
        var uri = Text(Blob(metadata, 1), 1);
        if (uri.Length == 0) uri = Text(metadata, 7).Split('\n')[0];
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : "";
    }
}
