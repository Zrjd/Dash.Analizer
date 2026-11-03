namespace DashAnalizer.Models;

/// <summary>Завершённый круг: индексы точек трека (границы включительно) и длительность в секундах.</summary>
public sealed record Lap(int Number, int StartIndex, int EndIndex, double DurationSeconds);