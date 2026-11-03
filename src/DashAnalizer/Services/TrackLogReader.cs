using System.Globalization;
using System.IO;
using System.Text;
using DashAnalizer.Models;

namespace DashAnalizer.Services;

/// <summary>
/// Чтение CSV-лога заезда.
/// Разбираются ТОЛЬКО колонки «Timestamp», «Latitude», «Longitude» (индексы находятся по заголовку);
/// файл открывается только на чтение, остальные колонки не затрагиваются.
/// </summary>
public static class TrackLogReader
{
    private const string DefaultLogRelativePath = @"DATA\LogFile.csv";

    /// <summary>Читает лог и возвращает точки трека. При любой ошибке файла — исключение с описанием.</summary>
    public static TrackLog Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Файл лога не найден: {path}", path);

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("Пустой файл лога: отсутствует строка заголовка.");

        var header = SplitCsvLine(headerLine);
        int iTime = FindColumn(header, "timestamp");
        int iLat = FindColumn(header, "latitude");
        int iLon = FindColumn(header, "longitude");

        if (iTime < 0 || iLat < 0 || iLon < 0)
            throw new InvalidDataException(
                "В заголовке не найдены обязательные колонки: Timestamp, Latitude (deg), Longitude (deg).");

        var points = new List<TrackPoint>();
        int skipped = 0;
        string? line;

        // накопители по всем колонкам — только для панели показателей (файл не изменяется)
        int columnCount = header.Length;
        var columnNumbers = new List<double>[columnCount];
        var columnTexts = new List<string?>[columnCount];
        var columnParsed = new int[columnCount];
        var columnFailed = new int[columnCount];

        for (int i = 0; i < columnCount; i++)
        {
            columnNumbers[i] = new List<double>();
            columnTexts[i] = new List<string?>();
        }

        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0)
                continue;

            var cells = SplitCsvLine(line);
            int needed = Math.Max(iTime, Math.Max(iLat, iLon));

            if (cells.Length <= needed ||
                !TryParseNumber(cells[iTime], out double timestamp) ||
                !TryParseNumber(cells[iLat], out double latitude) ||
                !TryParseNumber(cells[iLon], out double longitude))
            {
                skipped++;
                continue;
            }

            points.Add(new TrackPoint(timestamp, latitude, longitude));

            for (int i = 0; i < columnCount; i++)
            {
                var cell = i < cells.Length ? cells[i].Trim() : string.Empty;

                if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    columnNumbers[i].Add(value);
                    columnTexts[i].Add(null);
                    columnParsed[i]++;
                }
                else
                {
                    columnNumbers[i].Add(double.NaN);
                    columnTexts[i].Add(cell);
                    if (cell.Length > 0)
                        columnFailed[i]++;
                }
            }
        }

        if (points.Count == 0)
            throw new InvalidDataException("В файле нет ни одной корректной строки с Timestamp/Latitude/Longitude.");

        var columns = new List<LogColumn>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            columns.Add(new LogColumn(
                header[i].Trim().Trim('"'),
                i,
                columnNumbers[i].ToArray(),
                columnTexts[i].ToArray(),
                columnParsed[i],
                columnFailed[i]));
        }

        double minTimestamp = double.MaxValue, maxTimestamp = double.MinValue;
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;

        foreach (var p in points)
        {
            if (p.Timestamp < minTimestamp) minTimestamp = p.Timestamp;
            if (p.Timestamp > maxTimestamp) maxTimestamp = p.Timestamp;
            if (p.Latitude < minLat) minLat = p.Latitude;
            if (p.Latitude > maxLat) maxLat = p.Latitude;
            if (p.Longitude < minLon) minLon = p.Longitude;
            if (p.Longitude > maxLon) maxLon = p.Longitude;
        }

        return new TrackLog
        {
            FilePath = path,
            Points = points,
            Columns = columns,
            SkippedRows = skipped,
            StartTimestamp = minTimestamp,
            EndTimestamp = maxTimestamp,
            MinLatitude = minLat,
            MaxLatitude = maxLat,
            MinLongitude = minLon,
            MaxLongitude = maxLon,
        };
    }

    /// <summary>
    /// Ищет стандартный файл лога: DATA\LogFile.csv в текущем каталоге,
    /// затем в родительских каталогах от места запуска exe, затем по исходному пути из задания.
    /// </summary>
    public static string? FindDefaultLogPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, DefaultLogRelativePath),
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, DefaultLogRelativePath));

        candidates.Add(@"d:\GIT.AI\DashAnalizer\DATA\LogFile.csv");

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static int FindColumn(string[] header, string key)
    {
        for (int i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim().Trim('"');
            if (name.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    /// <summary>Разделение строки CSV с учётом кавычек (значение может содержать запятую).</summary>
    private static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells.ToArray();
    }
}