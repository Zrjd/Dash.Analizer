using DashAnalizer.Models;

namespace DashAnalizer.Services;

/// <summary>
/// Собирает вектор ускорения из колонок лога.
/// Продольное ускорение направлено вперёд, боковое — влево (ISO 8855),
/// курс берётся из колонки «Bearing (deg)» или из геометрии трека.
/// </summary>
public static class AccelerationCalculator
{
    private const string LongitudinalName = "Longitudinal Acceleration (G)";
    private const string LateralName = "Lateral Acceleration (G)";
    private const string BearingName = "Bearing (deg)";
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    /// Вектор для каждой точки трека; null — данных не хватает.
    /// Пустой массив, если в логе нет колонок ускорения.
    /// </summary>
    public static AccelerationSample?[] Compute(
        IReadOnlyList<TrackPoint> points,
        IReadOnlyList<LogColumn> columns)
    {
        var longitudinal = FindColumn(columns, LongitudinalName);
        var lateral = FindColumn(columns, LateralName);
        var bearing = FindColumn(columns, BearingName);

        if (longitudinal is null && lateral is null)
            return Array.Empty<AccelerationSample?>();

        var result = new AccelerationSample?[points.Count];

        for (int i = 0; i < points.Count; i++)
        {
            double forwardG = longitudinal?.NumberAt(i) ?? double.NaN;
            double leftG = lateral?.NumberAt(i) ?? double.NaN;

            bool hasForward = !double.IsNaN(forwardG);
            bool hasLeft = !double.IsNaN(leftG);
            if (!hasForward && !hasLeft)
                continue;

            if (!hasForward)
                forwardG = 0;
            if (!hasLeft)
                leftG = 0;

            double? heading = bearing?.NumberAt(i);
            if (heading is null || double.IsNaN(heading.Value))
                heading = BearingFromTrack(points, i);
            if (heading is null)
                continue;

            double radians = NormalizeDegrees(heading.Value) * Math.PI / 180.0;
            double sin = Math.Sin(radians);
            double cos = Math.Cos(radians);

            // forward = (east sin, north cos); left = (east -cos, north sin)
            double east = forwardG * sin - leftG * cos;
            double north = forwardG * cos + leftG * sin;

            double magnitude = Math.Sqrt(east * east + north * north);
            double direction = (Math.Atan2(east, north) * RadToDeg + 360.0) % 360.0;

            result[i] = new AccelerationSample(east, north, magnitude, direction);
        }

        return result;
    }

    private static LogColumn? FindColumn(IReadOnlyList<LogColumn> columns, string name)
    {
        foreach (var column in columns)
        {
            if (string.Equals(column.Name.Trim().Trim('"'), name, StringComparison.OrdinalIgnoreCase))
                return column;
        }

        foreach (var column in columns)
        {
            if (column.Name.Trim().Trim('"').StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return column;
        }

        return null;
    }

    /// <summary>Азимут по соседним точкам, если в логе нет курса.</summary>
    private static double? BearingFromTrack(IReadOnlyList<TrackPoint> points, int index)
    {
        int before = index - 1;
        while (before >= 0 && SamePosition(points[before], points[index]))
            before--;

        int after = index + 1;
        while (after < points.Count && SamePosition(points[after], points[index]))
            after++;

        int from = before >= 0 ? before : index;
        int to = after < points.Count ? after : index;

        if (from == to)
            return null;

        double dLat = points[to].Latitude - points[from].Latitude;
        double dLon = points[to].Longitude - points[from].Longitude;
        if (Math.Abs(dLat) < 1e-12 && Math.Abs(dLon) < 1e-12)
            return null;

        double latRadians = points[from].Latitude * Math.PI / 180.0;
        double east = dLon * Math.Cos(latRadians);
        double north = dLat;
        double degrees = Math.Atan2(east, north) * RadToDeg;

        return NormalizeDegrees(degrees);
    }

    private static bool SamePosition(TrackPoint a, TrackPoint b) =>
        Math.Abs(a.Latitude - b.Latitude) < 1e-9 &&
        Math.Abs(a.Longitude - b.Longitude) < 1e-9;

    private static double NormalizeDegrees(double degrees) =>
        (degrees % 360.0 + 360.0) % 360.0;
}
