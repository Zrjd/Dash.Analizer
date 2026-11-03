using System.IO;
using System.IO.Compression;

namespace DashAnalizer.Services;

/// <summary>
/// Папка кэша тайлов и перенос кэша через ZIP-архив.
/// Архив содержит только тайлы OSM и спутника, без файлов настроек.
/// </summary>
public static class TileCache
{
    private static readonly string[] AllowedExtensions = { ".png", ".jpg" };

    /// <summary>Корень кэша: OSM лежит в корне, спутник — в подпапке satellite.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashAnalizer",
        "Tiles");

    public static string OsmDirectory => Root;

    public static string SatelliteDirectory => Path.Combine(Root, "satellite");

    /// <summary>Экспортирует все тайлы в ZIP. Возвращает число записанных файлов.</summary>
    public static int Export(string zipPath)
    {
        if (!Directory.Exists(Root))
            return 0;

        int count = 0;
        using var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');

            FileStream source;
            try
            {
                // тайл может дописываться загрузчиком — читаем с общим доступом
                source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                continue;
            }

            using (source)
            {
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                using var target = entry.Open();
                source.CopyTo(target);
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Добавляет тайлы из ZIP к текущему кэшу. Совпадающие файлы перезаписываются.
    /// Пути проверяются, чтобы архив не мог записать файл вне папки кэша.
    /// </summary>
    public static int Import(string zipPath)
    {
        Directory.CreateDirectory(Root);
        string rootWithSeparator = Path.GetFullPath(Root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        int count = 0;
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');

            // каталоги и посторонние файлы пропускаем
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            string extension = Path.GetExtension(name);
            if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                continue;

            if (Path.IsPathRooted(name) || name.Contains(':'))
                continue;

            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
                continue;

            string destination = Path.GetFullPath(Path.Combine(Root, name));
            if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using var source = entry.Open();
            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
            count++;
        }

        return count;
    }
}
