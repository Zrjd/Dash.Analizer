using System.Globalization;
using System.IO;
using DashAnalizer.Models;

namespace DashAnalizer.Services;

/// <summary>
/// Коридор трассы: нормализация границ, проверка попадания точки и маска точек лога.
/// </summary>
public static class TrackZone
{
    /// <summary>Допуск на попадание точки ровно на границу, градусы.</summary>
    private const double BoundaryEpsilon = 1e-9;

    /// <summary>Допуск при проверке коллинеарности, произведение длин в градусах.</summary>
    private const double CrossEpsilon = 1e-12;

    /// <summary>Допуск тесселяции кривой — максимальное отклонение от сплайна, метры.</summary>
    private const double TessellationToleranceMeters = 1.0;

    /// <summary>Коэффициент перевода градусов длины в метры на экваторе.</summary>
    private const double DegreesToMeters = 111_132.0;

    /// <summary>
    /// Собирает область из двух границ. Возвращает null, если какая-то граница короче двух вершин.
    /// Правую границу разворачивает, если она нарисована навстречу левой.
    /// </summary>
    public static TrackArea? Create(TrackBoundary? left, TrackBoundary? right)
    {
        if (left is not { IsValid: true } || right is not { IsValid: true })
            return null;

        return new TrackArea(left, NormalizeRight(left, right));
    }

    /// <summary>Разворачивает правую границу, если её концы ближе к противоположным концам левой.</summary>
    private static TrackBoundary NormalizeRight(TrackBoundary left, TrackBoundary right)
    {
        var l0 = left.Vertices[0];
        var l1 = left.Vertices[^1];
        var r0 = right.Vertices[0];
        var r1 = right.Vertices[^1];

        double direct = DistanceMeters(l0, r0) + DistanceMeters(l1, r1);
        double reversed = DistanceMeters(l0, r1) + DistanceMeters(l1, r0);

        return reversed < direct
            ? new TrackBoundary(right.Vertices.Reverse().ToArray(), right.Kind)
            : new TrackBoundary(right.Vertices, right.Kind);
    }

    /// <summary>Тесселирует границу: для Polyline возвращает вершины как есть,
    /// для SmoothCurve — пробы Catmull-Rom с шагом, гарантирующим tolerance.</summary>
    private static List<GeoVertex> TessellateBoundary(TrackBoundary boundary)
    {
        var result = new List<GeoVertex>();

        if (boundary.Kind == BoundaryKind.Polyline)
        {
            // Для ломаной возвращаем вершины как есть (последнюю точку опустим, она уже есть в соседнем сегменте)
            result.AddRange(boundary.Vertices);
            if (boundary.Vertices.Count > 1)
                result.RemoveAt(result.Count - 1);
            return result;
        }

        // SmoothCurve — Catmull-Rom tessellation
        var vertices = boundary.Vertices;
        int n = vertices.Count;
        if (n < 2) return result;

        // Добавляем "замыленные" крайние точки: дублируем первую и последнюю
        // чтобы сплайн заканчивался в точках
        var extended = new List<GeoVertex>();
        extended.Add(vertices[0]); // p0
        extended.AddRange(vertices); // p1...pn
        extended.Add(vertices[^1]); // pn+1

        // Для Catmull-Rom нужны 4 точки на сегмент: i-1, i, i+1, i+2
        // Проходим от i=1 до i=n-1 (чтобы охватить все исходные вершины)
        for (int i = 1; i < extended.Count - 2; i++)
        {
            GeoVertex p0 = extended[i - 1];
            GeoVertex p1 = extended[i];
            GeoVertex p2 = extended[i + 1];
            GeoVertex p3 = extended[i + 2];

            // Вычисляем шаг t в зависимости от расстояния между p1 и p2
            double dLat = p2.Latitude - p1.Latitude;
            double dLon = p2.Longitude - p1.Longitude;
            double stepDeg = Math.Sqrt(dLat * dLat + dLon * dLon) * DegreesToMeters / TessellationToleranceMeters;
            int steps = Math.Max(2, (int)Math.Ceiling(stepDeg));

            for (int s = 0; s <= steps; s++)
            {
                double t = s / (double)steps;
                double t2 = t * t;
                double t3 = t2 * t;
                double latitude = 0.5 * ((2 * p1.Latitude) +
                    (-p0.Latitude + p2.Latitude) * t +
                    (2 * p0.Latitude - 5 * p1.Latitude + 4 * p2.Latitude - p3.Latitude) * t2 +
                    (-p0.Latitude + 3 * p1.Latitude - 3 * p2.Latitude + p3.Latitude) * t3);
                double longitude = 0.5 * ((2 * p1.Longitude) +
                    (-p0.Longitude + p2.Longitude) * t +
                    (2 * p0.Longitude - 5 * p1.Longitude + 4 * p2.Longitude - p3.Longitude) * t2 +
                    (-p0.Longitude + 3 * p1.Longitude - 3 * p2.Longitude + p3.Longitude) * t3);
                result.Add(new GeoVertex(latitude, longitude));
            }
        }

        // Убираем дубликаты подряд идущих одинаковых точек
        var cleaned = new List<GeoVertex>();
        foreach (var pt in result)
        {
            if (cleaned.Count == 0 ||
                Math.Abs(cleaned[^1].Latitude - pt.Latitude) > 1e-12 ||
                Math.Abs(cleaned[^1].Longitude - pt.Longitude) > 1e-12)
            {
                cleaned.Add(pt);
            }
        }

        return cleaned;
    }

    /// <summary>Вычисляет точку на Catmull-Rom сплайне для параметра t [0..1].</summary>
    private static GeoVertex CatmullRomPoint(double t, GeoVertex p0, GeoVertex p1, GeoVertex p2, GeoVertex p3)
    {
        double t2 = t * t;
        double t3 = t2 * t;

        double latitude = 0.5 * ((2 * p1.Latitude) +
            (-p0.Latitude + p2.Latitude) * t +
            (2 * p0.Latitude - 5 * p1.Latitude + 4 * p2.Latitude - p3.Latitude) * t2 +
            (-p0.Latitude + 3 * p1.Latitude - 3 * p2.Latitude + p3.Latitude) * t3);

        double longitude = 0.5 * ((2 * p1.Longitude) +
            (-p0.Longitude + p2.Longitude) * t +
            (2 * p0.Longitude - 5 * p1.Longitude + 4 * p2.Longitude - p3.Longitude) * t2 +
            (-p0.Longitude + 3 * p1.Longitude - 3 * p2.Longitude + p3.Longitude) * t3);

        return new GeoVertex(latitude, longitude);
    }

    /// <summary>Тесселирует entire TrackArea в плоский полигон GeoVertex list.</summary>
    private static List<GeoVertex> TessellatePolygon(TrackArea area)
    {
        var leftTess = TessellateBoundary(area.Left);
        var rightTess = TessellateBoundary(area.Right);

        // Замкнутый контур: левая ломаная + правая в обратном порядке
        var result = new List<GeoVertex>();
        result.AddRange(leftTess);
        result.AddRange(rightTess.OrderByDescending(v => v.Latitude)); // Fallback reverse
        return result;
    }

    /// <summary>Маска точек: true — точка внутри коридора или на его границе.</summary>
    public static bool[] BuildMask(IReadOnlyList<TrackPoint> points, TrackArea? area)
    {
        var mask = new bool[points.Count];

        if (area is null)
        {
            Array.Fill(mask, true);
            return mask;
        }

        // Тесселируем полигон для проверки попадания
        var polygon = TessellatePolygon(area);

        for (int i = 0; i < points.Count; i++)
            mask[i] = Contains(polygon, points[i].Latitude, points[i].Longitude);

        return mask;
    }

    public static int CountInside(IReadOnlyList<bool>? mask) =>
        mask is null ? 0 : mask.Count(value => value);

    /// <summary>Точка внутри замкнутой области; точка на границе считается внутри.</summary>
    public static bool Contains(List<GeoVertex> polygon, double latitude, double longitude)
    {
        if (polygon == null || polygon.Count < 3)
            return false;

        bool inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];

            if (IsOnSegment(a, b, latitude, longitude))
                return true;

            if ((a.Latitude > latitude) != (b.Latitude > latitude) &&
                longitude < (b.Longitude - a.Longitude) * (latitude - a.Latitude) /
                            (b.Latitude - a.Latitude) + a.Longitude)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Точка внутри замкнутой области; точка на границе считается внутри.</summary>
    public static bool Contains(TrackArea area, double latitude, double longitude)
    {
        return Contains(new List<GeoVertex>(area.Polygon), latitude, longitude);
    }

    /// <summary>
    /// Читает границу из текстового файла: по строке <c>lat lon</c>, InvariantCulture.
    /// Первая строка может быть маркером типа: "POLYLINE" или "SMOOTH".
    /// Повреждённый или короткий файл даёт null.
    /// </summary>
    public static TrackBoundary? TryLoadBoundary(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var lines = File.ReadAllLines(path);
            var vertices = new List<GeoVertex>();
            BoundaryKind kind = BoundaryKind.Polyline; //默认值

            int startIndex = 0;
            // Проверяем первую строку на маркер типа
            if (lines.Length > 0)
            {
                var firstParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (firstParts.Length == 1 &&
                    (firstParts[0].Equals("POLYLINE", StringComparison.OrdinalIgnoreCase) ||
                     firstParts[0].Equals("SMOOTH", StringComparison.OrdinalIgnoreCase)))
                {
                    kind = firstParts[0].Equals("SMOOTH", StringComparison.OrdinalIgnoreCase)
                        ? BoundaryKind.SmoothCurve
                        : BoundaryKind.Polyline;
                    startIndex = 1;
                }
                // ЕслиfirstParts.Length == 2, это значит первая строка — координаты, а не маркер
                // в этом случае startIndex оставляем 0
            }

            for (int i = startIndex; i < lines.Length; i++)
            {
                var parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                    continue;

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
                    continue;

                vertices.Add(new GeoVertex(latitude, longitude));
            }

            return vertices.Count >= 2 ? new TrackBoundary(vertices, kind) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Сохраняет границу: по строке <c>lat lon</c>, InvariantCulture.
    /// null удаляет файл. Ошибки записи обрабатывает вызывающий код.
    /// </summary>
    public static void SaveBoundary(string path, TrackBoundary? boundary)
    {
        if (boundary is { IsValid: true })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, boundary.Vertices.Select(vertex => string.Create(
                CultureInfo.InvariantCulture,
                $"{vertex.Latitude:0.#######} {vertex.Longitude:0.#######}")));
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool IsOnSegment(GeoVertex a, GeoVertex b, double latitude, double longitude)
    {
        double cross = (longitude - a.Longitude) * (b.Latitude - a.Latitude) -
                       (latitude - a.Latitude) * (b.Longitude - a.Longitude);
        if (Math.Abs(cross) > CrossEpsilon)
            return false;

        return longitude >= Math.Min(a.Longitude, b.Longitude) - BoundaryEpsilon &&
               longitude <= Math.Max(a.Longitude, b.Longitude) + BoundaryEpsilon &&
               latitude >= Math.Min(a.Latitude, b.Latitude) - BoundaryEpsilon &&
               latitude <= Math.Max(a.Latitude, b.Latitude) + BoundaryEpsilon;
    }

    private static double DistanceMeters(GeoVertex a, GeoVertex b)
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
