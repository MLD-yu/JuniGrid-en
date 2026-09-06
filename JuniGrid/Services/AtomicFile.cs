using System.IO;

namespace JuniGrid.Services;

/// <summary>
/// Text writes that survive a process kill/power loss without truncating the old file: write .tmp first, then atomically replace via File.Move
/// (if a crash happens mid-write, the disk holds either the old file or the complete new one).
/// Shared by JSON files written frequently, such as tasks.json / playtime.json / the translation cache.
/// Existing callers use a single writer per file (under a lock), but this still guards against concurrent writes to the same path:
/// unique tmp + short Move retries; no thread throws from concurrency, and the last writer wins.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            // v1.1.6: the tmp gets a random suffix — a fixed ".tmp" collides when two threads write the same path concurrently
            // (after A Moves the tmp away, B's Move misses and throws IOException).
            var tmp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
            try
            {
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                // the destination is briefly locked by another Move's ReplaceFile (concurrency can throw either UAE or IOException)
                // → retry later with a fresh tmp
                try { File.Delete(tmp); } catch { }
                Thread.Sleep(10 * attempt);
            }
        }
    }
}
