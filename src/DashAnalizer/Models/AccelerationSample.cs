namespace DashAnalizer.Models;

/// <summary>
/// Вектор ускорения в локальных осях: восток/север (G), модуль и компасный угол.
/// Угол 0° — север, далее по часовой стрелке.
/// </summary>
public readonly record struct AccelerationSample(
    double EastG,
    double NorthG,
    double MagnitudeG,
    double BearingDeg);
