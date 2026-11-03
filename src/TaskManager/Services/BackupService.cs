using System.IO.Compression;
using TaskManager.Data;

namespace TaskManager.Services;

/// <summary>Backup = zip with a consistent copy of data.db plus the attachments folder.</summary>
public static class BackupService
{
    public static void Export(Db db, AppPaths paths, string zipPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"tm-backup-{Guid.NewGuid():N}.db");
        try
        {
            db.CopyTo(tmp);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(tmp, "data.db");
            foreach (var file in Directory.EnumerateFiles(paths.Attachments))
                zip.CreateEntryFromFile(file, "attachments/" + Path.GetFileName(file), CompressionLevel.NoCompression);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Replaces the current data with the backup. The database must be closed by the caller.</summary>
    public static void Import(AppPaths paths, string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var dbEntry = zip.GetEntry("data.db") ?? throw new InvalidDataException("В архиве нет data.db");

        // Keep the current data next to it, just in case.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (File.Exists(paths.Database)) File.Copy(paths.Database, paths.Database + $".before-import-{stamp}", true);
        foreach (var ext in new[] { "-wal", "-shm" })
            if (File.Exists(paths.Database + ext)) File.Delete(paths.Database + ext);

        dbEntry.ExtractToFile(paths.Database, true);
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("attachments/") && e.Name.Length > 0))
            entry.ExtractToFile(Path.Combine(paths.Attachments, entry.Name), true);
    }
}
