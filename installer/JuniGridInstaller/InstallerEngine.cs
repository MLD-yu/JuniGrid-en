using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using SharpCompress.Compressors.LZMA;

namespace JuniGridInstaller;

/// <summary>One progress report: Status is the UI text, Fraction ∈ [0,1]; DoneBytes/TotalBytes are only valid during the file extraction phase.</summary>
public sealed record InstallProgress(string Status, double Fraction, long DoneBytes = 0, long TotalBytes = 0);

/// <summary>
/// Core install flow (mirrors the behavior of the old Inno Setup script installer.iss):
///   1. Close the running JuniGrid (must happen before touching the old uninstaller, otherwise file locks make it fail);
///   2. Detect a previous version (an Inno install with the same AppId) → silently run unins000.exe; our own GUI uninstall wizard is not in that category and is skipped;
///   3. Extract the embedded payload.lz (LZMA solid container of the publish\sc self-contained output, see PayloadTool) into the target directory;
///   4. Create the uninstaller + write the HKCU uninstall registry (reuses the old AppId so it can be uninstalled from Control Panel);
///   5. Start menu + optional desktop shortcuts.
/// Everything runs under HKCU / %LocalAppData%, matching the old PrivilegesRequired=lowest; no administrator rights needed.
/// </summary>
public sealed class InstallerEngine
{
    /// <summary>AppId from the old Inno script (installer.iss: AppId={{7E1B2C64-...}).</summary>
    public const string LegacyKey = "{7E1B2C64-9A4D-4C0E-9F61-3A5D8B2C4E10}_is1";
    private static readonly string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + LegacyKey;

    private const string ResourceName = "JuniGridInstaller.payload.lz";

    public static readonly string Version =
        Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    /// <summary>Default install directory: reuse the previous install location when present, otherwise %LocalAppData%\Programs\JuniGrid.</summary>
    public static string GetDefaultInstallDir()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            if (k?.GetValue("InstallLocation") is string loc && loc.Length > 4 && Directory.Exists(loc))
                return loc;
        }
        catch { }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Programs", "JuniGrid");
    }

    public (string? uninstallCmd, string? location) FindLegacyInstall()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            var cmd = (k?.GetValue("QuietUninstallString") ?? k?.GetValue("UninstallString")) as string;
            var loc = k?.GetValue("InstallLocation") as string;
            return (string.IsNullOrWhiteSpace(cmd) ? null : cmd, loc);
        }
        catch { return (null, null); }
    }

    public Task InstallAsync(string targetDir, bool desktopShortcut, IProgress<InstallProgress> progress, CancellationToken ct)
        => Task.Run(() => InstallCore(targetDir, desktopShortcut, progress, ct), ct);

    private void InstallCore(string targetDir, bool desktopShortcut, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        targetDir = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(targetDir);

        // Close the running JuniGrid first, then touch the old uninstaller — in the reverse order the files are
        // still locked, the old uninstaller pops a "file in use" error or hangs on an interstitial page, and the
        // silent uninstall effectively fails.
        progress.Report(new InstallProgress("Closing running JuniGrid…", 0.02));
        CloseRunningApp();

        // Test escape hatch: set JGINSTALLER_NOLEGACY=1 to skip the legacy silent uninstall
        if (Environment.GetEnvironmentVariable("JGINSTALLER_NOLEGACY") != "1")
        {
            var legacy = FindLegacyInstall();
            if (legacy.uninstallCmd is not null)
            {
                var (exe, _) = SplitCommand(legacy.uninstallCmd);
                var ownGui = exe.Length > 0 && Path.GetFileName(exe)
                    .Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase);
                if (!ownGui)
                {
                    progress.Report(new InstallProgress("Removing previous version…", 0.05));
                    RunLegacyUninstaller(legacy.uninstallCmd);
                }
                // When the uninstall entry is our own GUI uninstall wizard (Uninstall.exe = a copy of the main app),
                // it must never be launched mid-install: it would sit on its confirmation page waiting for a click,
                // and while the app is still running only the interstitial page is shown; after confirmation its
                // delayed rd /s /q self-delete could even wipe the freshly extracted files.
                // Its duties (close the app / remove shortcuts / clean the registry) are already fully covered by
                // this install's same-location overwrite + the registry/shortcut rewrite below, so skip it outright.
            }
        }

        long totalBytes = 0;
        using (var res = OpenResource())
        {
            var entries = ReadPayloadHeader(res, out long total);
            totalBytes = total;
            var root = Path.GetPathRoot(targetDir);
            if (root is not null && new DriveInfo(root).AvailableFreeSpace < total + 256L * 1024 * 1024)
                throw new InvalidOperationException("Not enough free space on the target drive. Free up some space and try again.");

            var props = new byte[5];
            res.ReadExactly(props);
            // Decoding ends naturally at the LZMA end-of-stream marker (the encoder's unknown-size mode always writes one);
            // each entry is written out exactly at the size recorded in its header, and an exhausted stream throws in the Read below
            using var lzma = LzmaStream.Create(props, res, leaveOpen: true);

            long done = 0, lastReport = 0;
            var buf = new byte[1 << 16];
            foreach (var (rel, size) in entries)
            {
                ct.ThrowIfCancellationRequested();
                var dest = SafePath(targetDir, rel);
                if (dest is null)
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var dst = File.Create(dest);
                long remaining = size;
                while (remaining > 0)
                {
                    int n = lzma.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                    if (n <= 0)
                        throw new IOException("Incomplete setup package data: " + rel);
                    dst.Write(buf, 0, n);
                    done += n;
                    remaining -= n;
                    if (done - lastReport > 3_500_000)
                    {
                        lastReport = done;
                        progress.Report(new InstallProgress("Installing files…", 0.10 + 0.87 * done / total, done, total));
                    }
                }
            }
        }
        progress.Report(new InstallProgress("Installing files…", 0.97));

        // Standalone uninstaller: copy the main app as Uninstall.exe; the app enters uninstall mode based on its file name
        progress.Report(new InstallProgress("Configuring uninstaller…", 0.97));
        File.Copy(Path.Combine(targetDir, "JuniGrid.exe"), Path.Combine(targetDir, "Uninstall.exe"), true);

        progress.Report(new InstallProgress("Creating shortcuts…", 0.985));
        CreateShortcuts(targetDir, desktopShortcut);
        WriteUninstallRegistry(targetDir);

        progress.Report(new InstallProgress("Installation complete", 1.0, totalBytes, totalBytes));
    }

    private static Stream OpenResource()
        => typeof(InstallerEngine).Assembly.GetManifestResourceStream(ResourceName)
           ?? throw new InvalidOperationException($"Missing embedded install payload {ResourceName}");

    /// <summary>Reads the JGP1 container header (format documented in PayloadTool/Program.cs), returning the file entry table and the total extracted size.</summary>
    private static List<(string rel, long size)> ReadPayloadHeader(Stream stream, out long totalSize)
    {
        Span<byte> head = stackalloc byte[8];
        stream.ReadExactly(head);
        if (!head[..4].SequenceEqual("JGP1"u8))
            throw new InvalidOperationException("Unexpected setup package format (expected a JGP1 container)");
        int count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
        var entries = new List<(string, long)>(count);
        long total = 0;
        Span<byte> entry = stackalloc byte[3 + sizeof(long)];
        for (int i = 0; i < count; i++)
        {
            stream.ReadExactly(entry);
            if (entry[0] != 0)
                throw new InvalidOperationException("Unknown setup package entry type");
            var pathBytes = new byte[BinaryPrimitives.ReadUInt16LittleEndian(entry[1..3])];
            stream.ReadExactly(pathBytes);
            long size = BinaryPrimitives.ReadInt64LittleEndian(entry[3..]);
            entries.Add((Encoding.UTF8.GetString(pathBytes), size));
            total += size;
        }
        totalSize = total;
        return entries;
    }

    /// <summary>Joins a path inside the package, rejecting malicious paths that escape the target directory; returns null when the entry is the root directory itself and can be skipped.</summary>
    private static string? SafePath(string root, string rel)
    {
        rel = rel.Replace('\\', '/');
        while (rel.StartsWith("./")) rel = rel[2..];
        if (rel.Length == 0 || rel == "." || rel.EndsWith('/')) rel = rel.TrimEnd('/');
        if (rel.Length == 0 || rel == ".") return null;
        var combined = Path.GetFullPath(Path.Combine(root, rel));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Illegal path inside the setup package: " + rel);
        return combined;
    }

    /// <summary>Splits an uninstall command of the form "C:\path\xxx.exe" args; exe is the empty string when it cannot be parsed.</summary>
    private static (string exe, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end < 0 ? ("", "") : (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        var sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    private static void RunLegacyUninstaller(string cmd)
    {
        try
        {
            var (exe, args) = SplitCommand(cmd);
            if (exe.Length == 0) return;
            // Add silent flags when the old Inno uninstaller (unins000.exe) was given none
            if (args.Length == 0 && Path.GetFileName(exe).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                args = "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES";
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(90_000);
        }
        catch
        {
            // A legacy uninstaller failure must not block the new install (same-directory overwrite + registry overwrite)
        }
    }

    private static void CloseRunningApp()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("JuniGrid"))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }
        catch { }
        Thread.Sleep(400);
    }

    private void CreateShortcuts(string dir, bool desktop)
    {
        var exe = Path.Combine(dir, "JuniGrid.exe");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)
            ?? throw new InvalidOperationException("Cannot create shortcuts (WScript.Shell unavailable)");

        void Make(string path)
        {
            var lnk = shell.CreateShortcut(path);
            lnk.TargetPath = exe;
            lnk.WorkingDirectory = dir;
            lnk.IconLocation = exe + ",0";
            lnk.Description = "JuniGrid — Stardew Valley companion";
            lnk.Save();
        }

        var group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "JuniGrid");
        Directory.CreateDirectory(group);
        Make(Path.Combine(group, "JuniGrid.lnk"));

        if (desktop)
        {
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Make(Path.Combine(desk, "JuniGrid.lnk"));
        }
    }

    private void WriteUninstallRegistry(string dir)
    {
        var exe = Path.Combine(dir, "JuniGrid.exe");
        var uninstallExe = Path.Combine(dir, "Uninstall.exe");
        long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(f).Length; } catch { }
        }

        // Uninstall entry: the standalone Uninstall.exe in the install directory (double-clicking starts the uninstall wizard)
        using var k = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        k.SetValue("DisplayName", "JuniGrid");
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", "JuniGrid");
        k.SetValue("InstallLocation", dir);
        k.SetValue("DisplayIcon", exe);
        k.SetValue("UninstallString", $"\"{uninstallExe}\"");
        k.SetValue("QuietUninstallString", $"\"{uninstallExe}\"");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
    }
}
