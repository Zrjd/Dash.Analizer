namespace DashAnalizer.Models;

/// <summary>Сектор в составе лучшего круга и признак ручного выбора.</summary>
public sealed record BestLapItem(Sector Sector, bool IsManual);

/// <summary>
/// Лучший круг, собранный из секторов разных кругов.
/// Один сектор на номер; время и дистанция — суммы выбранных секторов.
/// </summary>
public sealed record BestLap(
    IReadOnlyList<BestLapItem> Items,
    double TotalSeconds,
    double TotalDistanceMeters,
    int ExpectedCount)
{
    /// <summary>Средняя скорость по геометрии: дистанция / время.</summary>
    public double AverageSpeedKph =>
        TotalSeconds > 0 ? TotalDistanceMeters / TotalSeconds * 3.6 : double.NaN;
}
