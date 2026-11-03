using DashAnalizer.Models;

namespace DashAnalizer.Services;

/// <summary>
/// Нарезка трека на сектора по нескольким линиям поперёк трассы.
/// Пересечения всех линий сортируются по времени; сектор — интервал между соседними
/// пересечениями. Пересечение линии круга тоже делит сектор, поэтому нумерация
/// секторов начинается заново с 1 на каждом круге.
/// </summary>
public static class SectorCounter
{
    /// <summary>
    /// Все границы секторов: пересечения линий круга и линий секторов.
    /// Возвращает отсортированные индексы точек без повторов.
    /// insideMask — точки внутри коридора трассы; сегменты с точкой снаружи не считаются.
    /// </summary>
    public static List<int> CollectBoundaries(
        IReadOnlyList<TrackPoint> points,
        IReadOnlyList<LapLine> sectorLines,
        LapLine? lapLine,
        IReadOnlyList<bool>? insideMask = null)
    {
        var boundaries = new SortedSet<int>();

        if (lapLine is not null)
        {
            foreach (var crossing in LapCounter.FindCrossings(points, lapLine, insideMask: insideMask))
                boundaries.Add(crossing);
        }

        foreach (var line in sectorLines)
        {
            foreach (var crossing in LapCounter.FindCrossings(points, line, insideMask: insideMask))
                boundaries.Add(crossing);
        }

        return boundaries.ToList();
    }

    /// <summary>
    /// Сектора между соседними границами. Первый начинается с точки 0,
    /// последний заканчивается на последней точке лога.
    /// insideMask ограничивает дистанцию сектора сегментами внутри коридора трассы.
    /// </summary>
    public static List<Sector> BuildSectors(
        IReadOnlyList<TrackPoint> points,
        IReadOnlyList<int> boundaries,
        IReadOnlyList<int> lapCrossings,
        IReadOnlyList<bool>? insideMask = null)
    {
        var result = new List<Sector>();

        if (points.Count < 2)
            return result;

        var lapBoundaries = new HashSet<int>(lapCrossings);
        int start = 0;
        int lap = 1;
        int number = 1;

        foreach (int raw in boundaries)
        {
            int end = Math.Clamp(raw, 0, points.Count - 1);
            if (end <= start)
                continue;

            AddSector(result, points, start, end, lap, number, insideMask);

            start = end;
            if (lapBoundaries.Contains(end))
            {
                lap++;
                number = 1;
            }
            else
            {
                number++;
            }
        }

        if (start < points.Count - 1)
            AddSector(result, points, start, points.Count - 1, lap, number, insideMask);

        return result;
    }

    private static void AddSector(
        List<Sector> target,
        IReadOnlyList<TrackPoint> points,
        int start,
        int end,
        int lap,
        int number,
        IReadOnlyList<bool>? insideMask)
    {
        double duration = points[end].Timestamp - points[start].Timestamp;
        if (duration <= 0)
            return;

        bool useMask = insideMask is not null && insideMask.Count >= points.Count;
        double meters = 0;

        for (int i = start + 1; i <= end; i++)
        {
            if (useMask && (!insideMask![i - 1] || !insideMask[i]))
                continue;

            meters += HaversineMeters(points[i - 1], points[i]);
        }

        double speed = meters / duration * 3.6;

        target.Add(new Sector(lap, number, start, end, duration, meters, speed));
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
}
