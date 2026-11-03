using DashAnalizer.Models;

namespace DashAnalizer.Services;

/// <summary>
/// Пересечения траектории с отрезком «начало круга» и расчёт длительности кругов.
/// </summary>
public static class LapCounter
{
    /// <summary>
    /// Индексы точек, после которых траектория пересекла отрезок.
    /// minGap — минимальный интервал (в точках) между засчитываемыми пересечениями,
    /// чтобы дрожание GPS не давало ложных кругов.
    /// insideMask — точки внутри коридора трассы; сегмент с точкой снаружи не проверяется.
    /// </summary>
    public static List<int> FindCrossings(
        IReadOnlyList<TrackPoint> points,
        LapLine line,
        int minGap = 10,
        IReadOnlyList<bool>? insideMask = null)
    {
        var crossings = new List<int>();
        double ax = line.StartLongitude, ay = line.StartLatitude;
        double bx = line.EndLongitude, by = line.EndLatitude;
        bool useMask = insideMask is not null && insideMask.Count >= points.Count;

        for (int i = 1; i < points.Count; i++)
        {
            if (useMask && (!insideMask![i - 1] || !insideMask[i]))
                continue;

            double cx = points[i - 1].Longitude, cy = points[i - 1].Latitude;
            double dx = points[i].Longitude, dy = points[i].Latitude;

            if (!SegmentsIntersect(ax, ay, bx, by, cx, cy, dx, dy))
                continue;

            if (crossings.Count > 0 && i - crossings[^1] < minGap)
                continue;

            crossings.Add(i);
        }

        return crossings;
    }

    /// <summary>
    /// Длительности кругов: durations[k] — время от стартовой точки (или пересечения k-1)
    /// до пересечения k.
    /// </summary>
    public static List<double> ComputeLapDurations(IReadOnlyList<TrackPoint> points, IReadOnlyList<int> crossings)
    {
        var durations = new List<double>(crossings.Count);
        int previous = 0;

        foreach (var crossing in crossings)
        {
            durations.Add(points[crossing].Timestamp - points[previous].Timestamp);
            previous = crossing;
        }

        return durations;
    }

    /// <summary>Завершённые круги, построенные по пересечениям (границы-индексы включительно).</summary>
    public static List<Lap> BuildLaps(IReadOnlyList<TrackPoint> points, IReadOnlyList<int> crossings)
    {
        var laps = new List<Lap>(crossings.Count);
        int start = 0;

        foreach (var crossing in crossings)
        {
            laps.Add(new Lap(
                laps.Count + 1,
                start,
                crossing,
                points[crossing].Timestamp - points[start].Timestamp));
            start = crossing;
        }

        return laps;
    }

    /// <summary>
    /// Средняя скорость круга, км/ч: среднее значений колонки «Speed (kph)», если она есть;
    /// иначе — длина траектории (гаверсинус) / время круга.
    /// insideMask ограничивает и среднее, и длину точками внутри коридора трассы.
    /// </summary>
    public static double AverageSpeedKph(
        IReadOnlyList<TrackPoint> points,
        Lap lap,
        LogColumn? speedKphColumn = null,
        IReadOnlyList<bool>? insideMask = null)
    {
        bool useMask = insideMask is not null && insideMask.Count >= points.Count;

        if (speedKphColumn is not null)
        {
            double sum = 0;
            int count = 0;

            for (int i = lap.StartIndex; i <= lap.EndIndex && i < points.Count; i++)
            {
                if (useMask && !insideMask![i])
                    continue;

                var value = speedKphColumn.NumberAt(i);
                if (double.IsNaN(value))
                    continue;

                sum += value;
                count++;
            }

            if (count > 0)
                return sum / count;
        }

        double meters = 0;
        for (int i = lap.StartIndex + 1; i <= lap.EndIndex && i < points.Count; i++)
        {
            if (useMask && (!insideMask![i - 1] || !insideMask[i]))
                continue;

            meters += HaversineMeters(points[i - 1], points[i]);
        }

        return lap.DurationSeconds > 0 ? meters / lap.DurationSeconds * 3.6 : double.NaN;
    }

    private static double HaversineMeters(TrackPoint a, TrackPoint b)
    {
        const double earthRadius = 6371000.0;

        double dLat = (b.Latitude - a.Latitude) * Math.PI / 180.0;
        double dLon = (b.Longitude - a.Longitude) * Math.PI / 180.0;
        double lat1 = a.Latitude * Math.PI / 180.0;
        double lat2 = b.Latitude * Math.PI / 180.0;

        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return 2 * earthRadius * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    /// <summary>Строгое пересечение двух отрезков (коллинеарные случаи не считаются).</summary>
    private static bool SegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double d1 = Cross(bx - ax, by - ay, cx - ax, cy - ay);
        double d2 = Cross(bx - ax, by - ay, dx - ax, dy - ay);
        double d3 = Cross(dx - cx, dy - cy, ax - cx, ay - cy);
        double d4 = Cross(dx - cx, dy - cy, bx - cx, by - cy);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(double ux, double uy, double vx, double vy) => ux * vy - uy * vx;
}