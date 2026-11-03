namespace DashAnalizer.Models;

/// <summary>Вершина границы трассы в географических координатах.</summary>
public readonly record struct GeoVertex(double Latitude, double Longitude);

/// <summary>Тип границы трассы.</summary>
public enum BoundaryKind
{
    /// <summary>Прямая ломаная (уже существующий формат).</summary>
    Polyline,
    /// <summary>Сглаженная кривая Catmull-Rom через вершины.</summary>
    SmoothCurve
}

/// <summary>Ломаная вдоль одного края трассы.</summary>
public sealed record TrackBoundary(IReadOnlyList<GeoVertex> Vertices, BoundaryKind Kind = BoundaryKind.Polyline)
{
    /// <summary>Для коридора нужно не меньше двух вершин.</summary>
    public bool IsValid => Vertices.Count >= 2;
}

/// <summary>
/// Коридор трассы между левой и правой границами.
/// Правая граница хранится в том же направлении, что и левая,
/// поэтому замкнутый контур — левая ломаная и правая в обратном порядке.
/// </summary>
public sealed record TrackArea(TrackBoundary Left, TrackBoundary Right)
{
    /// <summary>Вершины замкнутой области для проверки попадания точки.</summary>
    public IReadOnlyList<GeoVertex> Polygon { get; } =
        Left.Vertices.Concat(Right.Vertices.Reverse()).ToArray();

    /// <summary>Тип левой границы.</summary>
    public BoundaryKind LeftKind => Left.Kind;

    /// <summary>Тип правой границы.</summary>
    public BoundaryKind RightKind => Right.Kind;
}
