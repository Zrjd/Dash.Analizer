namespace DashAnalizer.Models;

/// <summary>Отрезок «начало круга», расположенный поперёк трека.</summary>
public sealed record LapLine(
    double StartLatitude,
    double StartLongitude,
    double EndLatitude,
    double EndLongitude);