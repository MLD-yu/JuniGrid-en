// JuniGrid setup package packing tool:
//   dotnet run -c Release -- c <publish dir> <output file>   pack (JGP1 container + single-stream LZMA solid compression)
//   dotnet run -c Release -- x <payload file> <output dir>   unpack (same reading logic as InstallerEngine)
// Roughly 25-30% better compression than the old payload.zip's per-file Deflate; a full verification runs automatically after packing.
//
// JGP1 container layout:
//   4-byte magic "JGP1" | int32 entry count | entry table { byte kind(0=file), uint16 path length, UTF-8 path, int64 size } |
//   5-byte LZMA properties + LZMA stream (all file contents concatenated in entry order, solid compressed, end-of-stream marker at the tail)
// The extraction side (JuniGridInstaller.InstallerEngine) reads the same layout.

using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpCompress.Compressors.LZMA;

if (args.Length != 3 || (args[0] != "c" && args[0] != "x"))
{
    Console.Error.WriteLine($"Usage: PayloadTool c <publish dir> <output file> | x <payload file> <output dir> (received {args.Length} argument(s): [{string.Join(" | ", args)}])");
    return 1;
}
if (args[0] == "x")
{
    Extract(args[1], args[2]);
    Console.WriteLine($"Unpack complete: {args[2]}");
    return 0;
}

var inputDir = Path.GetFullPath(args[1]);
var outputFile = Path.GetFullPath(args[2]);
if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"Directory does not exist: {inputDir}");
    return 1;
}

// Build artifacts end users never need:
//  - *.pdb / *.map — debug symbols and frontend source maps
//  - Microsoft.DiaSymReader.Native.* / mscordaccore* / mscordbi — only needed for debugger/dump analysis;
//    pdb files are already not generated thanks to DebugType=none, runtime functionality is unaffected
string[] ExcludedFileNames =
[
    "Microsoft.DiaSymReader.Native.amd64.dll",
    "mscordbi.dll",
];
Func<string, bool> IsExcluded = path =>
    ExcludedFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
    || Path.GetFileName(path).StartsWith("mscordaccore", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
    || path.EndsWith(".map", StringComparison.OrdinalIgnoreCase);

var files = Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories)
    .Where(f => !IsExcluded(f))
    // Sort by extension + path: similar content sits adjacent, which uses the solid-LZMA dedup window most effectively
    .OrderBy(Path.GetExtension, StringComparer.OrdinalIgnoreCase)
    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
    .Select(f => (full: f, rel: Path.GetRelativePath(inputDir, f).Replace('\\', '/'), size: new FileInfo(f).Length))
    .ToList();

long totalRaw = files.Sum(f => f.size);
Console.WriteLine($"Entries: {files.Count} files, {totalRaw / 1048576.0:N1} MB raw");

// ---- Compress ----
var sw = System.Diagnostics.Stopwatch.StartNew();
Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
using (var outStream = File.Create(outputFile))
{
    Span<byte> header = stackalloc byte[8];
    "JGP1"u8.CopyTo(header);
    BinaryPrimitives.WriteInt32LittleEndian(header[4..], files.Count);
    outStream.Write(header);

    Span<byte> entry = stackalloc byte[3 + sizeof(long)];
    foreach (var f in files)
    {
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(f.rel);
        if (pathBytes.Length > ushort.MaxValue) throw new IOException($"Path too long: {f.rel}");
        entry[0] = 0; // kind = file
        BinaryPrimitives.WriteUInt16LittleEndian(entry[1..3], (ushort)pathBytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(entry[3..], f.size);
        outStream.Write(entry);
        outStream.Write(pathBytes);
    }

    // eos=true: the LZMA SDK already writes an end marker in the unknown-size encode mode;
    // declaring it explicitly and having the decoder "read until the end marker" keeps both sides semantically identical
    var props = new LzmaEncoderProperties(eos: true, dictionary: 1 << 26, numFastBytes: 273);
    var lzma = LzmaStream.Create(props, isLzma2: false, outStream);
    // SharpCompress does not write the 5-byte properties header for you; the caller must write it first (the decoder builds its stream from it)
    outStream.Write(lzma.Properties);
    long done = 0, lastReport = 0;
    var buf = new byte[1 << 20];
    foreach (var f in files)
    {
        using var src = File.OpenRead(f.full);
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            lzma.Write(buf, 0, n);
            done += n;
            if (done - lastReport >= 16L * 1024 * 1024)
            {
                lastReport = done;
                Console.WriteLine($"  Compressing {done / 1048576.0:N0}/{totalRaw / 1048576.0:N0} MB");
            }
        }
    }
    // The LZMA stream must be finished before outStream closes (writes the trailing sections)
    lzma.Dispose();
}
sw.Stop();
var packed = new FileInfo(outputFile).Length;
Console.WriteLine($"Compression complete: {packed / 1048576.0:N1} MB ({100.0 * packed / totalRaw:N1}%), took {sw.Elapsed.TotalMinutes:N1} min");

// ---- Verify: extract everything and compare SHA-256 against each source file ----
Console.WriteLine("Verifying (full extraction hash comparison)…");
using (var container = File.OpenRead(outputFile))
{
    Span<byte> head = stackalloc byte[8];
    container.ReadExactly(head);
    if (!head[..4].SequenceEqual("JGP1"u8)) throw new IOException("Output file magic mismatch");
    int count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
    if (count != files.Count) throw new IOException("Entry count mismatch");

    var entries = new List<(string rel, long size)>(count);
    Span<byte> entry = stackalloc byte[3 + sizeof(long)];
    for (int i = 0; i < count; i++)
    {
        container.ReadExactly(entry);
        if (entry[0] != 0) throw new IOException("Unknown entry type");
        var pathBytes = new byte[BinaryPrimitives.ReadUInt16LittleEndian(entry[1..3])];
        container.ReadExactly(pathBytes);
        entries.Add((System.Text.Encoding.UTF8.GetString(pathBytes), BinaryPrimitives.ReadInt64LittleEndian(entry[3..])));
    }

    var props = new byte[5];
    container.ReadExactly(props);
    // No outputSize passed: decoding ends naturally at the LZMA end-of-stream marker (the encoder's unknown-size mode always writes one)
    using var lzma = LzmaStream.Create(props, container, leaveOpen: true);

    var hashBuf = new byte[1 << 20];
    foreach (var (rel, size) in entries)
    {
        var srcPath = Path.Combine(inputDir, rel);
        if (new FileInfo(srcPath).Length != size) throw new IOException($"Size mismatch: {rel}");
        using var src = File.OpenRead(srcPath);
        using var shaA = SHA256.Create();
        using var shaB = SHA256.Create();
        var buf = hashBuf;
        long remaining = size;
        while (remaining > 0)
        {
            int n = lzma.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) throw new IOException($"LZMA stream ended early: {rel}");
            shaA.TransformBlock(buf, 0, n, null, 0);
            int m = src.Read(buf, 0, n);
            if (m != n) throw new IOException($"Could not read enough from source file: {rel}");
            shaB.TransformBlock(buf, 0, m, null, 0);
            remaining -= n;
        }
        shaA.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        shaB.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        if (!shaA.Hash!.AsSpan().SequenceEqual(shaB.Hash!))
            throw new IOException($"Hash mismatch: {rel}");
    }
    Console.WriteLine("Verification passed ✓");
}

Console.WriteLine($"Done: {outputFile}");
return 0;

// Unpack: the reading logic mirrors JuniGridInstaller.InstallerEngine line by line, used to verify the installer's extraction paths offline
static void Extract(string payloadPath, string outDir)
{
    using var stream = File.OpenRead(payloadPath);
    var entries = ReadHeader(stream);

    var props = new byte[5];
    stream.ReadExactly(props);
    using var lzma = LzmaStream.Create(props, stream, leaveOpen: true);

    Directory.CreateDirectory(outDir);
    var buf = new byte[1 << 16];
    foreach (var (rel, size) in entries)
    {
        var dest = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var dst = File.Create(dest);
        long remaining = size;
        while (remaining > 0)
        {
            int n = lzma.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
            if (n <= 0) throw new IOException("LZMA stream ended early: " + rel);
            dst.Write(buf, 0, n);
            remaining -= n;
        }
    }
}

static List<(string rel, long size)> ReadHeader(Stream stream)
{
    Span<byte> head = stackalloc byte[8];
    stream.ReadExactly(head);
    if (!head[..4].SequenceEqual("JGP1"u8))
        throw new IOException("Not a JGP1 container");
    int count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
    var entries = new List<(string, long)>(count);
    Span<byte> entry = stackalloc byte[3 + sizeof(long)];
    for (int i = 0; i < count; i++)
    {
        stream.ReadExactly(entry);
        if (entry[0] != 0) throw new IOException("Unknown entry type");
        var pathBytes = new byte[BinaryPrimitives.ReadUInt16LittleEndian(entry[1..3])];
        stream.ReadExactly(pathBytes);
        entries.Add((System.Text.Encoding.UTF8.GetString(pathBytes), BinaryPrimitives.ReadInt64LittleEndian(entry[3..])));
    }
    return entries;
}
