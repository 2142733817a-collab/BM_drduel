using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdimiDuel_Server.Duel;

// Each newline-delimited record is an independent gzip member. A crash cannot
// corrupt earlier rounds, and the journal may mix these with legacy JSON rows.
internal static class AdimiToolsAiLearningCodec
{
    internal const string Format = "adimi-gzip-json-v1";
    private static readonly uint[] CrcTable = MakeCrcTable();

    private sealed class Envelope
    {
        public string Format { get; set; } = AdimiToolsAiLearningCodec.Format;
        public int RawBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    internal static byte[] Encode(byte[] raw, int maxBytes)
    {
        if (raw.Length == 0 || raw.Length > maxBytes) throw new InvalidDataException("Episode exceeds maximum record size.");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(raw, 0, raw.Length);
        var envelope = new Envelope
        {
            RawBytes = raw.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(),
            Payload = Convert.ToBase64String(compressed.ToArray())
        };
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(envelope);
        // JSONL remains mixed-format compatible. Preserve a valid original row
        // if an incompressible payload would waste space or exceed the cap.
        return encoded.Length >= raw.Length || encoded.Length > maxBytes ? raw : encoded;
    }

    // Call only for a parsed row containing Format. Unknown or nested formats
    // are invalid, rather than being interpreted as an ordinary old episode.
    internal static byte[] Decode(JsonElement root, int maxBytes, bool forceManagedFallback = false)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Format", out JsonElement format)
            || format.ValueKind != JsonValueKind.String || format.GetString() != Format
            || !root.TryGetProperty("RawBytes", out JsonElement length) || length.ValueKind != JsonValueKind.Number
            || !length.TryGetInt32(out int rawLength) || rawLength <= 0 || rawLength > maxBytes
            || !root.TryGetProperty("Sha256", out JsonElement hash) || hash.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("Payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Malformed compressed episode.");
        string hashText = hash.GetString() ?? string.Empty;
        if (hashText.Length != 64 || hashText.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new InvalidDataException("Invalid compressed episode checksum.");
        byte[] expected = Convert.FromHexString(hashText);
        string base64 = payload.GetString() ?? string.Empty;
        if (base64.Length == 0 || base64.Length > maxBytes) throw new InvalidDataException("Compressed episode exceeds maximum record size.");
        byte[] compressed;
        try { compressed = Convert.FromBase64String(base64); }
        catch (FormatException exception) { throw new InvalidDataException("Invalid compressed episode payload.", exception); }
        if (Convert.ToBase64String(compressed) != base64) throw new InvalidDataException("Noncanonical compressed episode payload.");
        int footer = compressed.Length - 8;
        int payloadStart = ReadGzipHeader(compressed, footer);
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(footer, 4));
        uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(footer + 4, 4));
        if (storedSize != (uint)rawLength) throw new InvalidDataException("Gzip trailer size does not match.");
        byte[] raw;
        if (forceManagedFallback) raw = InflateStrictFallback(compressed, payloadStart, footer, rawLength);
        else
        {
            try { raw = InflateNative(compressed, payloadStart, footer, rawLength); }
            catch (DllNotFoundException) { raw = InflateStrictFallback(compressed, payloadStart, footer, rawLength); }
            catch (EntryPointNotFoundException) { raw = InflateStrictFallback(compressed, payloadStart, footer, rawLength); }
            catch (BadImageFormatException) { raw = InflateStrictFallback(compressed, payloadStart, footer, rawLength); }
        }
        if (Crc32(raw, 0, raw.Length) != storedCrc || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(raw), expected))
            throw new InvalidDataException("Compressed episode checksum or length does not match.");
        return raw;
    }

    // The runtime's native zlib PAL reports Z_STREAM_END and remaining input;
    // this proves the deflate section ended exactly before the gzip trailer.
    // When the PAL is unavailable, a slower one-byte source below retains the
    // same fail-closed boundary check without taking a new deployment dependency.
    private static byte[] InflateNative(byte[] compressed, int start, int end, int rawLength)
    {
        byte[] output = new byte[rawLength + 1];
        GCHandle inputHandle = default, outputHandle = default;
        var stream = new NativeZStream();
        bool initialized = false;
        try
        {
            inputHandle = GCHandle.Alloc(compressed, GCHandleType.Pinned);
            outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
            stream.NextIn = IntPtr.Add(inputHandle.AddrOfPinnedObject(), start);
            stream.NextOut = outputHandle.AddrOfPinnedObject();
            stream.AvailIn = checked((uint)(end - start));
            stream.AvailOut = checked((uint)output.Length);
            if (NativeInflateInit(ref stream, -15) != 0) throw new InvalidDataException("Could not initialize deflate decoder.");
            initialized = true;
            while (true)
            {
                uint inputBefore = stream.AvailIn, outputBefore = stream.AvailOut;
                int status = NativeInflate(ref stream, 0);
                if (status == 1) break;
                if (status != 0 || stream.AvailOut == 0 || (stream.AvailIn == inputBefore && stream.AvailOut == outputBefore))
                    throw new InvalidDataException("Incomplete or invalid deflate stream.");
            }
            if (stream.AvailIn != 0 || stream.AvailOut != 1) throw new InvalidDataException("Extra deflate data or mismatched length.");
            Array.Resize(ref output, rawLength);
            return output;
        }
        finally
        {
            if (initialized) { try { NativeInflateEnd(ref stream); } catch (Exception) { } }
            if (inputHandle.IsAllocated) inputHandle.Free();
            if (outputHandle.IsAllocated) outputHandle.Free();
        }
    }

    private static byte[] InflateStrictFallback(byte[] compressed, int start, int end, int rawLength)
    {
        using var input = new DeflateBoundaryStream(compressed, start, end);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        byte[] raw = new byte[rawLength];
        int offset = 0;
        while (offset < raw.Length)
        {
            int read = deflate.Read(raw, offset, raw.Length - offset);
            if (read == 0) throw new InvalidDataException("Truncated compressed episode.");
            offset += read;
        }
        if (deflate.ReadByte() != -1 || !input.AtExactEnd) throw new InvalidDataException("Extra or incomplete deflate data.");
        return raw;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeZStream
    {
        internal IntPtr NextIn;
        internal IntPtr NextOut;
        internal IntPtr Message;
        internal IntPtr State;
        internal uint AvailIn;
        internal uint AvailOut;
    }

    [DllImport("System.IO.Compression.Native", EntryPoint = "CompressionNative_InflateInit2_", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeInflateInit(ref NativeZStream stream, int windowBits);
    [DllImport("System.IO.Compression.Native", EntryPoint = "CompressionNative_Inflate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeInflate(ref NativeZStream stream, int flush);
    [DllImport("System.IO.Compression.Native", EntryPoint = "CompressionNative_InflateEnd", CallingConvention = CallingConvention.Cdecl)]
    private static extern int NativeInflateEnd(ref NativeZStream stream);

    private static int ReadGzipHeader(byte[] bytes, int footer)
    {
        if (footer < 10 || bytes[0] != 0x1f || bytes[1] != 0x8b || bytes[2] != 8 || (bytes[3] & 0xe0) != 0)
            throw new InvalidDataException("Invalid gzip header.");
        byte flags = bytes[3];
        int position = 10;
        if ((flags & 4) != 0)
        {
            if (position > footer - 2) throw new InvalidDataException("Truncated gzip extra field.");
            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2));
            position += 2;
            if (length > footer - position) throw new InvalidDataException("Truncated gzip extra field.");
            position += length;
        }
        if ((flags & 8) != 0) SkipZeroTerminated(bytes, ref position, footer);
        if ((flags & 16) != 0) SkipZeroTerminated(bytes, ref position, footer);
        if ((flags & 2) != 0)
        {
            if (position > footer - 2) throw new InvalidDataException("Truncated gzip header checksum.");
            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2));
            if ((ushort)Crc32(bytes, 0, position) != stored) throw new InvalidDataException("Gzip header checksum does not match.");
            position += 2;
        }
        if (position >= footer) throw new InvalidDataException("Missing gzip deflate data.");
        return position;
    }

    private static void SkipZeroTerminated(byte[] bytes, ref int position, int end)
    {
        bool found = false;
        while (position < end)
        {
            if (bytes[position++] == 0) { found = true; break; }
        }
        if (!found) throw new InvalidDataException("Truncated gzip header field.");
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    private static uint Crc32(byte[] bytes, int offset, int length)
    {
        uint value = 0xffffffffu;
        for (int i = offset; i < offset + length; i++) value = CrcTable[(value ^ bytes[i]) & 0xff] ^ (value >> 8);
        return ~value;
    }

    private sealed class DeflateBoundaryStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _end;
        private int _position;
        private int _sentinels;

        internal DeflateBoundaryStream(byte[] bytes, int start, int end) { _bytes = bytes; _position = start; _end = end; }
        internal bool AtExactEnd => _position == _end && _sentinels == 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            int value = NextByte();
            if (value < 0) return 0;
            buffer[offset] = (byte)value;
            return 1;
        }
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            int value = NextByte();
            if (value < 0) return 0;
            buffer[0] = (byte)value;
            return 1;
        }
        private int NextByte()
        {
            if (_position < _end) return _bytes[_position++];
            if (_sentinels++ < 8) return 0xff;
            return -1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
