namespace DashAnalizer.Models;

/// <summary>
/// Точка трека. Хранятся только те колонки лога, которые используются для отображения на карте:
/// Timestamp, Latitude, Longitude. Остальные колонки CSV не читаются и не изменяются.
/// </summary>
public readonly record struct TrackPoint(double Timestamp, double Latitude, double Longitude);