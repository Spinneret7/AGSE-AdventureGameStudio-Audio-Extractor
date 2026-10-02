using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class Program
{
    const string ManifestName = "manifest.txt";

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int j = 0; j < 8; j++)
                r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : (r << 1);
            table[i] = r;
        }
        return table;
    }

    static uint Crc(byte[] d, int off, int len)
    {
        uint c = 0;
        for (int i = 0; i < len; i++)
            c = (c << 8) ^ CrcTable[((c >> 24) & 0xFF) ^ d[off + i]];
        return c;
    }

    struct Page
    {
        public int Length;
        public byte Flags;
        public uint Serial;
        public int BodyOffset;
        public int BodyLength;
    }

    static bool TryReadPage(byte[] d, int pos, out Page page)
    {
        page = default;
        if (pos < 0 || pos + 27 > d.Length) return false;
        if (d[pos] != 'O' || d[pos + 1] != 'g' || d[pos + 2] != 'g' || d[pos + 3] != 'S') return false;
        if (d[pos + 4] != 0) return false;

        int segs = d[pos + 26];
        int headerLen = 27 + segs;
        if (pos + headerLen > d.Length) return false;

        int bodyLen = 0;
        for (int i = 0; i < segs; i++) bodyLen += d[pos + 27 + i];

        int total = headerLen + bodyLen;
        if (pos + total > d.Length) return false;

        uint stored = BitConverter.ToUInt32(d, pos + 22);
        var copy = new byte[total];
        Buffer.BlockCopy(d, pos, copy, 0, total);
        copy[22] = copy[23] = copy[24] = copy[25] = 0;
        if (Crc(copy, 0, total) != stored) return false;

        page.Length = total;
        page.Flags = d[pos + 5];
        page.Serial = BitConverter.ToUInt32(d, pos + 14);
        page.BodyOffset = pos + headerLen;
        page.BodyLength = bodyLen;
        return true;
    }

    static bool IsVorbisStart(byte[] d, Page p)
    {
        if ((p.Flags & 0x02) == 0 || p.BodyLength < 7) return false;
        int o = p.BodyOffset;
        return d[o] == 0x01 && d[o + 1] == 'v' && d[o + 2] == 'o' && d[o + 3] == 'r'
            && d[o + 4] == 'b' && d[o + 5] == 'i' && d[o + 6] == 's';
    }

    class Stream
    {
        public int Offset;
        public int Length;
        public uint Serial;
        public bool HasEos;
    }

    static List<Stream> ScanStreams(byte[] data)
    {
        var result = new List<Stream>();
        var marker = new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S' };
        int pos = 0;

        while (pos < data.Length - 27)
        {
            int found = data.AsSpan(pos).IndexOf(marker);
            if (found < 0) break;
            int start = pos + found;

            if (!TryReadPage(data, start, out Page first) || !IsVorbisStart(data, first))
            {
                pos = start + 1;
                continue;
            }

            uint serial = first.Serial;
            int p = start, end = start;
            bool eos = false;

            while (TryReadPage(data, p, out Page cur))
            {
                p += cur.Length;
                end = p;
                if (cur.Serial == serial && (cur.Flags & 0x04) != 0) { eos = true; break; }
            }

            result.Add(new Stream { Offset = start, Length = end - start, Serial = serial, HasEos = eos });
            pos = end;
        }
        return result;
    }

    static string StreamFileName(string baseName, int index) => $"{baseName}_{index:D4}.ogg";

    static int Extract(string input, string outDir)
    {
        string baseName = Path.GetFileNameWithoutExtension(input);
        byte[] data = File.ReadAllBytes(input);
        Console.WriteLine($"Scanning {input} ({data.Length:N0} bytes)...");

        var streams = ScanStreams(data);
        if (streams.Count == 0)
        {
            Console.WriteLine("No Ogg Vorbis streams found.");
            return 0;
        }

        Directory.CreateDirectory(outDir);
        var manifest = new List<string>
        {
            "# OggExtract manifest - do not edit",
            "size\t" + data.Length
        };

        for (int i = 0; i < streams.Count; i++)
        {
            var s = streams[i];
            string name = StreamFileName(baseName, i);
            string path = Path.Combine(outDir, name);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                fs.Write(data, s.Offset, s.Length);

            manifest.Add($"stream\t{s.Offset}\t{s.Length}\t{name}");
            Console.WriteLine($"[{i:D4}] offset 0x{s.Offset:X8}  size {s.Length:N0}  serial {s.Serial:X8}"
                + (s.HasEos ? "" : "  (no EOS page - truncated?)") + $"  -> {name}");
        }

        File.WriteAllLines(Path.Combine(outDir, ManifestName), manifest);
        Console.WriteLine($"Done. Extracted {streams.Count} stream(s) to: {outDir}");
        return 0;
    }

    static int Repack(string input, string folder, bool pad)
    {
        string manifestPath = Path.Combine(folder, ManifestName);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Manifest not found: {manifestPath}\nRun the E command first.");
            return 1;
        }

        byte[] original = File.ReadAllBytes(input);
        long expectedSize = -1;
        var entries = new List<(int Offset, int Length, string Name)>();

        foreach (var line in File.ReadAllLines(manifestPath))
        {
            if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts[0] == "size") expectedSize = long.Parse(parts[1]);
            else if (parts[0] == "stream")
                entries.Add((int.Parse(parts[1]), int.Parse(parts[2]), parts[3]));
        }

        if (expectedSize != original.Length)
        {
            Console.Error.WriteLine("The container's size differs from the one the manifest was made from.\n" +
                                    "Use the same unmodified container that you extracted.");
            return 1;
        }

        entries = entries.OrderBy(e => e.Offset).ToList();
        string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
            Path.GetFileNameWithoutExtension(input) + "_new" + Path.GetExtension(input));

        int cursor = 0, changed = 0;
        bool sizeChanged = false;

        using (var ms = new MemoryStream())
        {
            foreach (var e in entries)
            {
                ms.Write(original, cursor, e.Offset - cursor);

                string oggPath = Path.Combine(folder, e.Name);
                byte[] replacement;
                if (File.Exists(oggPath))
                {
                    replacement = File.ReadAllBytes(oggPath);
                }
                else
                {
                    Console.WriteLine($"  WARNING: {e.Name} missing - keeping original data.");
                    replacement = null;
                }

                if (replacement == null)
                {
                    ms.Write(original, e.Offset, e.Length);
                }
                else
                {
                    bool same = replacement.Length == e.Length &&
                                replacement.AsSpan().SequenceEqual(original.AsSpan(e.Offset, e.Length));
                    if (!same) changed++;

                    if (pad && replacement.Length > e.Length)
                    {
                        Console.Error.WriteLine($"ERROR: {e.Name} is {replacement.Length - e.Length:N0} bytes larger than the " +
                                                "original slot, so /pad can't keep the layout. Re-encode at a lower bitrate " +
                                                "or repack without /pad.");
                        return 1;
                    }

                    ms.Write(replacement, 0, replacement.Length);
                    if (pad && replacement.Length < e.Length)
                        ms.Write(new byte[e.Length - replacement.Length], 0, e.Length - replacement.Length);
                    else if (replacement.Length != e.Length)
                        sizeChanged = true;

                    if (!same)
                        Console.WriteLine($"  replaced {e.Name}: {e.Length:N0} -> {replacement.Length:N0} bytes");
                }
                cursor = e.Offset + e.Length;
            }

            ms.Write(original, cursor, original.Length - cursor);
            File.WriteAllBytes(outPath, ms.ToArray());
        }

        Console.WriteLine($"Done. {entries.Count} stream(s) written, {changed} modified -> {outPath}");
        if (sizeChanged)
            Console.WriteLine("NOTE: stream sizes changed, so anything in the container that stores offsets/sizes " +
                              "(a header or file table) was NOT updated. If the game/program rejects the file, " +
                              "try /pad or fix the table.");
        return 0;
    }

    static void Usage()
    {
        Console.WriteLine("AGSE AdventureGameStudio - Audio Extractor / repacker\n");
        Console.WriteLine("A simple tool which can Extract the Audio file like Speech.vox");
        Console.WriteLine("  AGSE E <container> [folder]          Extract streams");
        Console.WriteLine("  AGSE R <container> [folder] [/pad]   Repack into <container>_new");
        Console.WriteLine("\n  folder defaults to <container name>_extracted");
    }

    static int Main(string[] args)
    {
        if (args.Length < 2) { Usage(); return 1; }

        string cmd = args[0].TrimStart('-', '/').ToUpperInvariant();
        bool pad = args.Skip(2).Any(a => a.Equals("/pad", StringComparison.OrdinalIgnoreCase) ||
                                         a.Equals("-pad", StringComparison.OrdinalIgnoreCase));
        var rest = args.Skip(1).Where(a => !a.Equals("/pad", StringComparison.OrdinalIgnoreCase) &&
                                           !a.Equals("-pad", StringComparison.OrdinalIgnoreCase)).ToArray();

        string input = rest[0];
        if (!File.Exists(input))
        {
            Console.Error.WriteLine("File not found: " + input);
            return 1;
        }

        string folder = rest.Length > 1
            ? rest[1]
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
                           Path.GetFileNameWithoutExtension(input) + "_extracted");

        switch (cmd)
        {
            case "E": return Extract(input, folder);
            case "R": return Repack(input, folder, pad);
            default: Usage(); return 1;
        }
    }
}