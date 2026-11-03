using DashAnalizer.Models;

namespace DashAnalizer.Services;

/// <summary>Результат чтения лога заезда. Файл открывается исключительно на чтение.</summary>
public sealed record TrackLog
{
    public required string FilePath { get; init; }

    public required IReadOnlyList<TrackPoint> Points { get; init; }

    /// <summary>Все колонки файла (для панели показателей); значения выровнены по Points.</summary>
    public required IReadOnlyList<LogColumn> Columns { get; init; }

    /// <summary>Число строк, которые не удалось разобрать (пустые или с некорректными координатами).</summary>
    public int SkippedRows { get; init; }

    public double StartTimestamp { get; init; }
    public double EndTimestamp { get; init; }
    public double MinLatitude { get; init; }
    public double MaxLatitude { get; init; }
    public double MinLongitude { get; init; }
    public double MaxLongitude { get; init; }
}