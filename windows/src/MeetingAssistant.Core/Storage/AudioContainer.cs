using System.Buffers.Binary;
using System.Text;

namespace MeetingAssistant.Core.Storage;

/// <summary>
/// Header-level access to the app's audio files (WAV and Mac CAF, linear PCM): length without
/// decoding, sample-exact copies of a time range in the file's own container and format (used
/// by trim and split, spec 01 "Editing recordings"), and a Float32 CAF writer for dictation
/// recovery files (identical to the Mac's `AudioIO.writeCAF`).
/// </summary>
public static class AudioContainer
{
    /// Layout of a PCM audio file.
    public sealed record Info(
        string Container,     // "wav" or "caf"
        double SampleRate,
        int Channels,
        int BytesPerFrame,
        long DataOffset,      // first audio byte
        long DataBytes,       // audio bytes (whole frames)
        long HeaderEnd,       // offset of the data chunk header
        int Bits = 16,
        bool IsFloat = false,
        bool BigEndian = false)
    {
        public long Frames => BytesPerFrame > 0 ? DataBytes / BytesPerFrame : 0;
        public double Seconds => SampleRate > 0 ? Frames / SampleRate : 0;
    }

    /// Reads the header of a WAV or CAF file. Unfinished files (size fields 0, -1 or past the end of
    /// the file, as after a crash) are read up to the end of the file.
    public static Info Probe(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Probe(fs);
    }

    static Info Probe(Stream s)
    {
        var head = new byte[12];
        ReadExactly(s, head, 0, 12);
        var magic = Encoding.ASCII.GetString(head, 0, 4);
        if (magic == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "WAVE") return ProbeWav(s);
        if (magic == "caff") { s.Position = 8; return ProbeCaf(s); }
        throw new InvalidDataException("Unsupported audio file");
    }

    static Info ProbeWav(Stream s)
    {
        long pos = 12;
        int channels = 1, bits = 16, blockAlign = 0, format = 1;
        double rate = 16_000;
        var hdr = new byte[8];
        while (pos + 8 <= s.Length)
        {
            s.Position = pos;
            ReadExactly(s, hdr, 0, 8);
            var id = Encoding.ASCII.GetString(hdr, 0, 4);
            long len = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
            long body = pos + 8;
            if (id == "fmt ")
            {
                var f = new byte[Math.Min(26, (int)len)];
                ReadExactly(s, f, 0, f.Length);
                format = BinaryPrimitives.ReadUInt16LittleEndian(f);
                if (format == 0xFFFE && f.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(24));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(14));
            }
            else if (id == "data")
            {
                long avail = s.Length - body;
                long bytes = len == 0 || len > avail || len == 0xFFFFFFFF ? avail : len;
                int bpf = blockAlign > 0 ? blockAlign : Math.Max(1, channels) * bits / 8;
                bytes -= bytes % Math.Max(1, bpf);
                return new Info("wav", rate, channels, bpf, body, bytes, pos, bits, format == 3, false);
            }
            pos = body + len + (len & 1);
        }
        throw new InvalidDataException("WAV file without data");
    }

    static Info ProbeCaf(Stream s)
    {
        long pos = 8;
        int channels = 1, bpf = 2, bits = 16;
        bool isFloat = false, big = true;
        double rate = 16_000;
        var hdr = new byte[12];
        while (pos + 12 <= s.Length)
        {
            s.Position = pos;
            ReadExactly(s, hdr, 0, 12);
            var id = Encoding.ASCII.GetString(hdr, 0, 4);
            long len = BinaryPrimitives.ReadInt64BigEndian(hdr.AsSpan(4));
            long body = pos + 12;
            if (id == "desc")
            {
                var d = new byte[32];
                ReadExactly(s, d, 0, 32);
                rate = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(d));
                var flags = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(12));
                isFloat = (flags & 1) != 0;
                big = (flags & 2) == 0;
                var bytesPerPacket = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16));
                channels = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(24));
                bits = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(28));
                bpf = bytesPerPacket > 0 ? bytesPerPacket : Math.Max(1, channels) * bits / 8;
            }
            else if (id == "data")
            {
                long avail = s.Length - body - 4;     // 4 bytes edit count
                long bytes = len < 4 || len - 4 > avail ? avail : len - 4;
                bytes = Math.Max(0, bytes);
                bytes -= bytes % Math.Max(1, bpf);
                return new Info("caf", rate, channels, bpf, body + 4, bytes, pos, bits, isFloat, big);
            }
            if (len < 0) break;
            pos = body + len;
        }
        throw new InvalidDataException("CAF file without data");
    }

    /// Length in seconds of an audio file; when the header cannot be read, estimated from the file
    /// size as 16 kHz 16-bit mono after a 4096-byte header (spec 01 "Crash recovery").
    public static double DurationOrEstimate(string path)
    {
        try { return Probe(path).Seconds; }
        catch
        {
            try { return Math.Max(0, new FileInfo(path).Length - 4096) / 32_000.0; } catch { return 0; }
        }
    }

    /// Copies `start ..&lt; end` seconds of `src` into `dst` in the same container and sample format,
    /// sample-exactly (frame = seconds × the file's rate, truncated). Everything in the source header
    /// before the data chunk is kept (so a Mac CAF keeps its layout).
    public static void CopyRange(string src, string dst, double start, double end)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var info = Probe(input);
        long first = (long)(start * info.SampleRate);
        long last = Math.Min(info.Frames, (long)(end * info.SampleRate));
        first = Math.Clamp(first, 0, info.Frames);
        long frames = Math.Max(0, last - first);
        long bytes = frames * info.BytesPerFrame;
        if (File.Exists(dst)) File.Delete(dst);
        using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write);
        // Header: everything before the data chunk header.
        input.Position = 0;
        CopyBytes(input, output, info.HeaderEnd);
        var hdr = new byte[12];
        if (info.Container == "wav")
        {
            Encoding.ASCII.GetBytes("data").CopyTo(hdr, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), (uint)Math.Min(uint.MaxValue, bytes));
            output.Write(hdr, 0, 8);
        }
        else
        {
            Encoding.ASCII.GetBytes("data").CopyTo(hdr, 0);
            BinaryPrimitives.WriteInt64BigEndian(hdr.AsSpan(4), bytes + 4);
            output.Write(hdr, 0, 12);
            var edit = new byte[4];
            input.Position = info.DataOffset - 4;
            ReadExactly(input, edit, 0, 4);
            output.Write(edit, 0, 4);
        }
        input.Position = info.DataOffset + first * info.BytesPerFrame;
        CopyBytes(input, output, bytes);
        if (info.Container == "wav")
        {
            if ((bytes & 1) == 1) output.WriteByte(0);
            // RIFF size = file length − 8.
            output.Flush();
            var riff = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(riff, (uint)Math.Min(uint.MaxValue, output.Length - 8));
            output.Position = 4;
            output.Write(riff, 0, 4);
        }
    }

    /// Copies `start ..&lt; end` seconds of a WAV or CAF file into a new WAV file with the same sample
    /// format (rate, channels, bits, integer/float), sample-exactly. Used for new tracks on Windows
    /// (part 2 of a split), which are always WAV (spec 01 "Track file format across platforms").
    public static void CopyRangeAsWav(string src, string dst, double start, double end)
    {
        var info = Probe(src);
        if (info.Container == "wav") { CopyRange(src, dst, start, end); return; }
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long first = Math.Clamp((long)(start * info.SampleRate), 0, info.Frames);
        long last = Math.Min(info.Frames, (long)(end * info.SampleRate));
        long bytes = Math.Max(0, last - first) * info.BytesPerFrame;
        if (File.Exists(dst)) File.Delete(dst);
        using var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write);
        var h = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)Math.Min(uint.MaxValue, 36 + bytes + (bytes & 1)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), (ushort)(info.IsFloat ? 3 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), (ushort)info.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)info.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)(info.SampleRate * info.BytesPerFrame));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), (ushort)info.BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), (ushort)info.Bits);
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)Math.Min(uint.MaxValue, bytes));
        output.Write(h, 0, 44);
        input.Position = info.DataOffset + first * info.BytesPerFrame;
        int sampleBytes = Math.Max(1, info.Bits / 8);
        var buf = new byte[(1 << 20) / info.BytesPerFrame * info.BytesPerFrame];
        long remaining = bytes;
        while (remaining > 0)
        {
            int n = input.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) break;
            if (info.BigEndian && sampleBytes > 1)
                for (int i = 0; i + sampleBytes <= n; i += sampleBytes) Array.Reverse(buf, i, sampleBytes);
            output.Write(buf, 0, n);
            remaining -= n;
        }
        if ((bytes & 1) == 1) output.WriteByte(0);
    }

    /// Writes 16 kHz mono samples as a Float32 little-endian CAF file laid out like the Mac's
    /// (`desc`, a `free` chunk so the audio starts at byte 4096, then `data`).
    public static void WriteCafFloat32(string path, ReadOnlySpan<float> samples, double sampleRate = 16_000)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var b = new byte[4096];
        int p = 0;
        Encoding.ASCII.GetBytes("caff").CopyTo(b, 0); p = 4;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), 1); p += 2;          // version
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), 0); p += 2;          // flags
        Encoding.ASCII.GetBytes("desc").CopyTo(b, p); p += 4;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(p), 32); p += 8;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(p), BitConverter.DoubleToInt64Bits(sampleRate)); p += 8;
        Encoding.ASCII.GetBytes("lpcm").CopyTo(b, p); p += 4;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), 3); p += 4;          // float | little endian
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), 4); p += 4;          // bytes per packet
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), 1); p += 4;          // frames per packet
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), 1); p += 4;          // channels
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), 32); p += 4;         // bits
        // free chunk up to the data chunk header at 4096 − 16
        int dataHeader = 4096 - 16;
        Encoding.ASCII.GetBytes("free").CopyTo(b, p); p += 4;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(p), dataHeader - (p + 8)); p += 8;
        fs.Write(b, 0, dataHeader);
        var dh = new byte[16];
        Encoding.ASCII.GetBytes("data").CopyTo(dh, 0);
        BinaryPrimitives.WriteInt64BigEndian(dh.AsSpan(4), samples.Length * 4L + 4);
        // edit count 0
        fs.Write(dh, 0, 16);
        var buf = new byte[Math.Min(samples.Length, 65_536) * 4];
        int i = 0;
        while (i < samples.Length)
        {
            int n = Math.Min(samples.Length - i, buf.Length / 4);
            for (int k = 0; k < n; k++) BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(k * 4), samples[i + k]);
            fs.Write(buf, 0, n * 4);
            i += n;
        }
    }

    static void CopyBytes(Stream from, Stream to, long count)
    {
        var buf = new byte[1 << 20];
        while (count > 0)
        {
            int n = from.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) break;
            to.Write(buf, 0, n);
            count -= n;
        }
    }

    static void ReadExactly(Stream s, byte[] buf, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, offset + read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }
}
