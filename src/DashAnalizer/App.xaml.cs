using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using DashAnalizer.Models;
using DashAnalizer.Services;

namespace DashAnalizer;

/// <summary>
/// Interaction logic for App.xaml.
/// Главное окно создаётся вручную (без StartupUri), чтобы поддержать
/// диагностический режим <c>--stats</c>, работающий без GUI.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Диагностика без GUI: --stats [файл-отчёта] [путь-к-логу].
        if (e.Args.Length > 0 && string.Equals(e.Args[0], "--stats", StringComparison.OrdinalIgnoreCase))
        {
            RunStats(e.Args);
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void RunStats(string[] args)
    {
        var outPath = args.Length > 1 && args[1].Length > 0
            ? args[1]
            : Path.Combine(Path.GetTempPath(), "DashAnalizer_stats.txt");

        try
        {
            var logPath = args.Length > 2 && args[2].Length > 0
                ? args[2]
                : TrackLogReader.FindDefaultLogPath()
                  ?? throw new FileNotFoundException("Файл LogFile.csv не найден.");

            var log = TrackLogReader.Read(logPath);
            var report = new StringBuilder();
            report.AppendLine($"File: {log.FilePath}");
            report.AppendLine($"Points: {log.Points.Count.ToString(CultureInfo.InvariantCulture)}");
            report.AppendLine($"SkippedRows: {log.SkippedRows.ToString(CultureInfo.InvariantCulture)}");
            report.AppendLine($"Timestamp: {log.StartTimestamp.ToString("0.000", CultureInfo.InvariantCulture)} .. {log.EndTimestamp.ToString("0.000", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Latitude: {log.MinLatitude.ToString("0.#######", CultureInfo.InvariantCulture)} .. {log.MaxLatitude.ToString("0.#######", CultureInfo.InvariantCulture)}");
            report.AppendLine($"Longitude: {log.MinLongitude.ToString("0.#######", CultureInfo.InvariantCulture)} .. {log.MaxLongitude.ToString("0.#######", CultureInfo.InvariantCulture)}");

            // необязательные 5-й и 6-й аргументы: файлы левой и правой границ трассы
            IReadOnlyList<bool>? zoneMask = null;
            if (args.Length > 5 && args[4].Length > 0 && args[5].Length > 0)
            {
                var left = TrackZone.TryLoadBoundary(args[4]);
                var right = TrackZone.TryLoadBoundary(args[5]);
                var area = TrackZone.Create(left, right);

                if (area is null)
                {
                    report.AppendLine("ZoneSpec: invalid");
                }
                else
                {
                    zoneMask = TrackZone.BuildMask(log.Points, area);
                    report.AppendLine(
                        $"ZoneInside: {TrackZone.CountInside(zoneMask)} / {log.Points.Count}");
                }
            }

            // необязательный 4-й аргумент: отрезок "начало круга" lat1,lon1,lat2,lon2
            if (args.Length > 3 && args[3].Length > 0)
            {
                var parts = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 4 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var startLat) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var startLon) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var endLat) &&
                    double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var endLon))
                {
                    var line = new LapLine(startLat, startLon, endLat, endLon);
                    var crossings = LapCounter.FindCrossings(log.Points, line, insideMask: zoneMask);
                    var laps = LapCounter.BuildLaps(log.Points, crossings);
                    var speedColumn = log.Columns
                        .FirstOrDefault(c => string.Equals(c.Name, "Speed (kph)", StringComparison.OrdinalIgnoreCase));

                    report.AppendLine($"LapCrossings: {crossings.Count.ToString(CultureInfo.InvariantCulture)}");
                    report.AppendLine("Laps: " + string.Join("; ",
                        laps.Select(l => l.DurationSeconds.ToString("0.000", CultureInfo.InvariantCulture))));
                    report.AppendLine("LapSpeedsKph: " + string.Join("; ",
                        laps.Select(l => LapCounter.AverageSpeedKph(log.Points, l, speedColumn, zoneMask)
                            .ToString("0.0", CultureInfo.InvariantCulture))));
                }
                else
                {
                    report.AppendLine("LapSpec: invalid");
                }
            }

            File.WriteAllText(outPath, report.ToString());
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(outPath, "ERROR: " + ex.Message);
            }
            catch
            {
                // путь для отчёта недоступен — от ошибки диагностики проект не зависит
            }
        }
    }
}

