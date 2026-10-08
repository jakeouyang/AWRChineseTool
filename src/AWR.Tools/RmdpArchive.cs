using System.Buffers.Binary;
using System.Text;

namespace AWR.Tools;

public sealed class RmdpFileEntry
{
    public uint NameHash;
    public byte[] Pad1 = new byte[4];
    public long NextFile;
    public long PrevFolder;
    public byte[] Flags = new byte[8];
    public long NameOffset;
    public ulong Offset;
    public ulong Size;
    public uint DataCrc;
    public byte[] Pad2 = new byte[4];
    public string Name = "";
    public string Path = "";
}

public sealed class RmdpFolderEntry
{
    public uint NameHash;
    public byte[] Pad1 = new byte[4];
    public long NextNeighbourFolder;
    public long PrevFolder;
    public byte[] Pad2 = new byte[8];
    public long NameOffset;
    public long NextLowerFolder;
    public long NextFile;
    public string Name = "";
}

public sealed class RmdpArchive
{
    public byte EndianFlag;
    public uint Version;
    public string PathPrefix = "";
    public byte[] PrefixBlock = Array.Empty<byte>(); // everything between nameSize field and first folder entry
    public List<RmdpFolderEntry> Folders = new();
    public List<RmdpFileEntry> Files = new();
    public byte[] NamePool = Array.Empty<byte>();

    public static RmdpArchive Load(string binPath)
    {
        var data = File.ReadAllBytes(binPath);
        var arc = new RmdpArchive();
        arc.EndianFlag = data[0];
        bool be = arc.EndianFlag != 0;
        arc.Version = be ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1)) : BitConverter.ToUInt32(data, 1);
        if (arc.Version != 2)
            throw new InvalidDataException($"Unsupported .bin version {arc.Version} (expected 2, Alan Wake Remastered)");
        uint numFolders = be ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(5)) : BitConverter.ToUInt32(data, 5);
        uint numFiles = be ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(9)) : BitConverter.ToUInt32(data, 9);
        int nameSize = (int)(be ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(13)) : BitConverter.ToUInt32(data, 13));

        int prefixStart = 17;
        int prefixEnd = Array.IndexOf(data, (byte)0, prefixStart);
        arc.PathPrefix = Encoding.ASCII.GetString(data, prefixStart, prefixEnd - prefixStart);
        int structStart = prefixEnd + 1 + 120;
        arc.PrefixBlock = data[..structStart];

        if (data.Length < nameSize) throw new InvalidDataException("bin too small for name pool");
        arc.NamePool = data[^nameSize..];

        int pos = structStart;
        for (int i = 0; i < numFolders; i++)
        {
            var e = new RmdpFolderEntry();
            e.NameHash = ReadU32(data, ref pos, be);
            e.Pad1 = data[pos..(pos + 4)]; pos += 4;
            e.NextNeighbourFolder = ReadI64(data, ref pos, be);
            e.PrevFolder = ReadI64(data, ref pos, be);
            e.Pad2 = data[pos..(pos + 8)]; pos += 8;
            e.NameOffset = ReadI64(data, ref pos, be);
            e.NextLowerFolder = ReadI64(data, ref pos, be);
            e.NextFile = ReadI64(data, ref pos, be);
            arc.Folders.Add(e);
        }
        for (int i = 0; i < numFiles; i++)
        {
            var e = new RmdpFileEntry();
            e.NameHash = ReadU32(data, ref pos, be);
            e.Pad1 = data[pos..(pos + 4)]; pos += 4;
            e.NextFile = ReadI64(data, ref pos, be);
            e.PrevFolder = ReadI64(data, ref pos, be);
            e.Flags = data[pos..(pos + 8)]; pos += 8;
            e.NameOffset = ReadI64(data, ref pos, be);
            e.Offset = ReadU64(data, ref pos, be);
            e.Size = ReadU64(data, ref pos, be);
            e.DataCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;
            e.Pad2 = data[pos..(pos + 4)]; pos += 4;
            arc.Files.Add(e);
        }
        if (pos != data.Length - nameSize)
            throw new InvalidDataException($"Entry table size mismatch: parsed to {pos}, name pool starts at {data.Length - nameSize}");

        // resolve names
        foreach (var f in arc.Folders) f.Name = arc.NameAt(f.NameOffset);
        foreach (var f in arc.Files)
        {
            f.Name = arc.NameAt(f.NameOffset);
            f.Path = BuildPath(arc.Folders, f.PrevFolder, f.Name);
        }
        return arc;
    }

    private static string BuildPath(List<RmdpFolderEntry> folders, long prevFolder, string name)
    {
        var parts = new List<string> { name };
        long p = prevFolder;
        var seen = new HashSet<long>();
        while (p > 0 && p < folders.Count && seen.Add(p))
        {
            var folder = folders[(int)p];
            if (!string.IsNullOrEmpty(folder.Name)) parts.Add(folder.Name);
            p = folder.PrevFolder;
        }
        parts.Reverse();
        return string.Join('\\', parts);
    }

    public string NameAt(long offset)
    {
        if (offset < 0 || offset >= NamePool.Length) return "";
        int start = (int)offset;
        int end = Array.IndexOf(NamePool, (byte)0, start);
        if (end < 0) end = NamePool.Length;
        return Encoding.ASCII.GetString(NamePool, start, end - start);
    }

    private static uint ReadU32(byte[] d, ref int pos, bool be)
    {
        uint v = be ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos)) : BitConverter.ToUInt32(d, pos);
        pos += 4; return v;
    }
    private static long ReadI64(byte[] d, ref int pos, bool be)
    {
        long v = be ? BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(pos)) : BitConverter.ToInt64(d, pos);
        pos += 8; return v;
    }
    private static ulong ReadU64(byte[] d, ref int pos, bool be)
    {
        ulong v = be ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(pos)) : BitConverter.ToUInt64(d, pos);
        pos += 8; return v;
    }

    public byte[] ReadFileData(string rmdpPath, RmdpFileEntry e)
    {
        using var fs = new FileStream(rmdpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek((long)e.Offset, SeekOrigin.Begin);
        var buf = new byte[(long)e.Size];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) throw new IOException($"Unexpected EOF in rmdp at {e.Offset}");
            read += n;
        }
        return buf;
    }

    public void ExtractAll(string rmdpPath, string outDir, Func<string, bool>? filter = null, Action<string, long>? progress = null)
    {
        foreach (var e in Files)
        {
            if (filter != null && !filter(e.Path)) continue;
            string target = Path.Combine(outDir, e.Path.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var data = ReadFileData(rmdpPath, e);
            uint crc = Crc32.Compute(data);
            if (crc != e.DataCrc)
                Console.Error.WriteLine($"[warn] crc mismatch for {e.Path}: stored {e.DataCrc:X8}, actual {crc:X8}");
            File.WriteAllBytes(target, data);
            progress?.Invoke(e.Path, (long)e.Size);
        }
    }

    /// <summary>
    /// Repacks the archive: entries whose path is present in <paramref name="replacements"/>
    /// get new content (sizes/crcs updated), everything else streams from the original .rmdp.
    /// Folder table, names, flags and header junk are preserved byte-for-byte.
    /// </summary>
    public void Repack(string rmdpPath, Dictionary<string, byte[]> replacements, string outBinPath, string outRmdpPath)
    {
        if (EndianFlag == 0) throw new InvalidDataException("Only big-endian AWR archives can be written.");
        foreach (var path in replacements.Keys)
            if (!Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Replacement path is not in the archive: " + path);
        var ordered = Files.OrderBy(f => f.Offset).ToList();

        using var src = new FileStream(rmdpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var rmdp = new FileStream(outRmdpPath, FileMode.Create, FileAccess.Write);
        var newOffsets = new Dictionary<RmdpFileEntry, ulong>();
        foreach (var e in ordered)
        {
            ulong offset = (ulong)rmdp.Position;
            newOffsets[e] = offset;
            if (replacements.TryGetValue(e.Path, out var data))
            {
                e.Size = (ulong)data.Length;
                e.DataCrc = Crc32.Compute(data);
                rmdp.Write(data, 0, data.Length);
            }
            else
            {
                src.Seek((long)e.Offset, SeekOrigin.Begin);
                var buf = new byte[(long)e.Size];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = src.Read(buf, read, buf.Length - read);
                    if (n <= 0) throw new IOException($"Unexpected EOF in source rmdp at {e.Offset}");
                    read += n;
                }
                rmdp.Write(buf, 0, buf.Length);
            }
            int pad = (int)((8 - (rmdp.Position % 8)) % 8);
            for (int i = 0; i < pad; i++) rmdp.WriteByte(0);
        }
        long endPos = rmdp.Position;
        int endPad = (int)((8 - (endPos % 8)) % 8);
        for (int i = 0; i < endPad; i++) rmdp.WriteByte(0);
        rmdp.Dispose();

        using var bin = new FileStream(outBinPath, FileMode.Create, FileAccess.Write);
        bin.Write(PrefixBlock); // header incl. endian flag, counts, nameSize, prefix, padding
        foreach (var f in Folders) WriteFolder(bin, f);
        foreach (var e in Files) WriteFile(bin, e, newOffsets[e]);
        bin.Write(NamePool);
        bin.Dispose();
    }

    private static void WriteFolder(Stream s, RmdpFolderEntry e)
    {
        Span<byte> b = stackalloc byte[56];
        BinaryPrimitives.WriteUInt32BigEndian(b, e.NameHash);
        e.Pad1.AsSpan().CopyTo(b[4..]);
        BinaryPrimitives.WriteInt64BigEndian(b[8..], e.NextNeighbourFolder);
        BinaryPrimitives.WriteInt64BigEndian(b[16..], e.PrevFolder);
        e.Pad2.AsSpan().CopyTo(b[24..]);
        BinaryPrimitives.WriteInt64BigEndian(b[32..], e.NameOffset);
        BinaryPrimitives.WriteInt64BigEndian(b[40..], e.NextLowerFolder);
        BinaryPrimitives.WriteInt64BigEndian(b[48..], e.NextFile);
        s.Write(b);
    }

    private static void WriteFile(Stream s, RmdpFileEntry e, ulong newOffset)
    {
        Span<byte> b = stackalloc byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(b, e.NameHash);
        e.Pad1.AsSpan().CopyTo(b[4..]);
        BinaryPrimitives.WriteInt64BigEndian(b[8..], e.NextFile);
        BinaryPrimitives.WriteInt64BigEndian(b[16..], e.PrevFolder);
        e.Flags.AsSpan().CopyTo(b[24..]);
        BinaryPrimitives.WriteInt64BigEndian(b[32..], e.NameOffset);
        BinaryPrimitives.WriteUInt64BigEndian(b[40..], newOffset);
        BinaryPrimitives.WriteUInt64BigEndian(b[48..], e.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(b[56..], e.DataCrc);
        e.Pad2.AsSpan().CopyTo(b[60..]);
        s.Write(b);
    }
}
