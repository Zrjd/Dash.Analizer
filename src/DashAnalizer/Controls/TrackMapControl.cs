using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DashAnalizer.Models;
using DashAnalizer.Services;

namespace DashAnalizer.Controls;

/// <summary>
/// Карта трека заезда (проекция Web Mercator):
/// подложка OpenStreetMap с кэшем и откатом на координатную сетку при отсутствии сети,
/// линия трека и пройденной части, маркеры старта/финиша/текущей позиции,
/// зум колесом (вокруг курсора), панорамирование мышью, масштабная линейка.
/// </summary>
public class TrackMapControl : FrameworkElement
{
    // ---------- зависимости ----------

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track),
        typeof(IReadOnlyList<TrackPoint>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTrackChanged));

    public static readonly DependencyProperty CurrentIndexProperty = DependencyProperty.Register(
        nameof(CurrentIndex),
        typeof(int),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowTilesProperty = DependencyProperty.Register(
        nameof(ShowTiles),
        typeof(bool),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnShowTilesChanged));

    public IReadOnlyList<TrackPoint>? Track
    {
        get => (IReadOnlyList<TrackPoint>?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>Индекс текущей точки трека (по времени).</summary>
    public int CurrentIndex
    {
        get => (int)GetValue(CurrentIndexProperty);
        set => SetValue(CurrentIndexProperty, value);
    }

    /// <summary>Показывать подложку OpenStreetMap (false — только координатная сетка).</summary>
    public bool ShowTiles
    {
        get => (bool)GetValue(ShowTilesProperty);
        set => SetValue(ShowTilesProperty, value);
    }

    public static readonly DependencyProperty ShowSatelliteProperty = DependencyProperty.Register(
        nameof(ShowSatellite),
        typeof(bool),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Показывать спутниковые тайлы вместо OSM.</summary>
    public bool ShowSatellite
    {
        get => (bool)GetValue(ShowSatelliteProperty);
        set => SetValue(ShowSatelliteProperty, value);
    }

    public static readonly DependencyProperty IsPlacingLapLineProperty = DependencyProperty.Register(
        nameof(IsPlacingLapLine),
        typeof(bool),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnPlacementChanged));

    public static readonly DependencyProperty LapLineProperty = DependencyProperty.Register(
        nameof(LapLine),
        typeof(LapLine),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Режим размещения отрезка «начало круга» двумя кликами по карте.</summary>
    public bool IsPlacingLapLine
    {
        get => (bool)GetValue(IsPlacingLapLineProperty);
        set => SetValue(IsPlacingLapLineProperty, value);
    }

    /// <summary>Отрезок «начало круга» (null — не задан).</summary>
    public LapLine? LapLine
    {
        get => (LapLine?)GetValue(LapLineProperty);
        set => SetValue(LapLineProperty, value);
    }

    /// <summary>Срабатывает при завершении размещения отрезка (второй клик по карте).</summary>
    public event Action<LapLine>? LapLineCompleted;

    public static readonly DependencyProperty LapsProperty = DependencyProperty.Register(
        nameof(Laps),
        typeof(IReadOnlyList<Lap>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HiddenLapsProperty = DependencyProperty.Register(
        nameof(HiddenLaps),
        typeof(IReadOnlyCollection<int>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BestLapSectorsProperty = DependencyProperty.Register(
        nameof(BestLapSectors),
        typeof(IReadOnlyList<Sector>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LapColorsProperty = DependencyProperty.Register(
        nameof(LapColors),
        typeof(IReadOnlyDictionary<int, Color>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Границы завершённых кругов — разбиение трека и подсветка на карте.</summary>
    public IReadOnlyList<Lap>? Laps
    {
        get => (IReadOnlyList<Lap>?)GetValue(LapsProperty);
        set => SetValue(LapsProperty, value);
    }

    /// <summary>Номера кругов, скрытых с карты. Пусто — виден весь трек.</summary>
    public IReadOnlyCollection<int>? HiddenLaps
    {
        get => (IReadOnlyCollection<int>?)GetValue(HiddenLapsProperty);
        set => SetValue(HiddenLapsProperty, value);
    }

    /// <summary>Состав лучшего круга: рисуются только эти сектора.</summary>
    public IReadOnlyList<Sector>? BestLapSectors
    {
        get => (IReadOnlyList<Sector>?)GetValue(BestLapSectorsProperty);
        set => SetValue(BestLapSectorsProperty, value);
    }

    /// <summary>Левая граница коридора трассы (null — не задана).</summary>
    public static readonly DependencyProperty LeftBoundaryProperty = DependencyProperty.Register(
        nameof(LeftBoundary),
        typeof(TrackBoundary),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBoundaryChanged));

    /// <summary>Правая граница коридора трассы (null — не задана).</summary>
    public static readonly DependencyProperty RightBoundaryProperty = DependencyProperty.Register(
        nameof(RightBoundary),
        typeof(TrackBoundary),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnBoundaryChanged));

    /// <summary>Маска точек внутри коридора; null — фильтр не действует.</summary>
    public static readonly DependencyProperty ZoneMaskProperty = DependencyProperty.Register(
        nameof(ZoneMask),
        typeof(IReadOnlyList<bool>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsPlacingLeftBoundaryProperty = DependencyProperty.Register(
        nameof(IsPlacingLeftBoundary),
        typeof(bool),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnPlacementChanged));

    public static readonly DependencyProperty IsPlacingRightBoundaryProperty = DependencyProperty.Register(
        nameof(IsPlacingRightBoundary),
        typeof(bool),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnPlacementChanged));

    public TrackBoundary? LeftBoundary
    {
        get => (TrackBoundary?)GetValue(LeftBoundaryProperty);
        set => SetValue(LeftBoundaryProperty, value);
    }

    public TrackBoundary? RightBoundary
    {
        get => (TrackBoundary?)GetValue(RightBoundaryProperty);
        set => SetValue(RightBoundaryProperty, value);
    }

    public IReadOnlyList<bool>? ZoneMask
    {
        get => (IReadOnlyList<bool>?)GetValue(ZoneMaskProperty);
        set => SetValue(ZoneMaskProperty, value);
    }

    /// <summary>Режим ввода левой границы трассы кликами по карте.</summary>
    public bool IsPlacingLeftBoundary
    {
        get => (bool)GetValue(IsPlacingLeftBoundaryProperty);
        set => SetValue(IsPlacingLeftBoundaryProperty, value);
    }

    /// <summary>Режим ввода правой границы трассы кликами по карте.</summary>
    public bool IsPlacingRightBoundary
    {
        get => (bool)GetValue(IsPlacingRightBoundaryProperty);
        set => SetValue(IsPlacingRightBoundaryProperty, value);
    }

    /// <summary>Срабатывает при завершении левой границы.</summary>
    public event Action<TrackBoundary>? LeftBoundaryCompleted;

    /// <summary>Срабатывает при завершении правой границы.</summary>
    public event Action<TrackBoundary>? RightBoundaryCompleted;

    /// <summary>Срабатывает после переноса, вставки или удаления вершины левой границы.</summary>
    public event Action<TrackBoundary>? LeftBoundaryEdited;

    /// <summary>Срабатывает после переноса, вставки или удаления вершины правой границы.</summary>
    public event Action<TrackBoundary>? RightBoundaryEdited;

    /// <summary>Индивидуальные цвета кругов (ключ — номер круга); сегменты рисуются поверх трека.</summary>
    public IReadOnlyDictionary<int, Color>? LapColors
    {
        get => (IReadOnlyDictionary<int, Color>?)GetValue(LapColorsProperty);
        set => SetValue(LapColorsProperty, value);
    }

    public static readonly DependencyProperty SectorLinesProperty = DependencyProperty.Register(
        nameof(SectorLines),
        typeof(IReadOnlyList<LapLine>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsPlacingSectorLineProperty = DependencyProperty.Register(
        nameof(IsPlacingSectorLine),
        typeof(bool),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnPlacementChanged));

    public static readonly DependencyProperty SectorsProperty = DependencyProperty.Register(
        nameof(Sectors),
        typeof(IReadOnlyList<Sector>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightedSectorProperty = DependencyProperty.Register(
        nameof(HighlightedSector),
        typeof(int),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Линии границ секторов, расположенные поперёк трека.</summary>
    public IReadOnlyList<LapLine>? SectorLines
    {
        get => (IReadOnlyList<LapLine>?)GetValue(SectorLinesProperty);
        set => SetValue(SectorLinesProperty, value);
    }

    /// <summary>Режим размещения линии границы сектора двумя кликами по карте.</summary>
    public bool IsPlacingSectorLine
    {
        get => (bool)GetValue(IsPlacingSectorLineProperty);
        set => SetValue(IsPlacingSectorLineProperty, value);
    }

    /// <summary>Готовые сектора: номера, интервалы, время, дистанция и скорость.</summary>
    public IReadOnlyList<Sector>? Sectors
    {
        get => (IReadOnlyList<Sector>?)GetValue(SectorsProperty);
        set => SetValue(SectorsProperty, value);
    }

    /// <summary>Индекс показываемого сектора; -1 — весь трек.</summary>
    public int HighlightedSector
    {
        get => (int)GetValue(HighlightedSectorProperty);
        set => SetValue(HighlightedSectorProperty, value);
    }

    /// <summary>Срабатывает при завершении линии границы сектора (второй клик по карте).</summary>
    public event Action<LapLine>? SectorLineCompleted;

    public static readonly DependencyProperty AccelerationsProperty = DependencyProperty.Register(
        nameof(Accelerations),
        typeof(IReadOnlyList<AccelerationSample?>),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowAccelerationProperty = DependencyProperty.Register(
        nameof(ShowAcceleration),
        typeof(bool),
        typeof(TrackMapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Вектор ускорения для каждой точки трека (null — данных нет).</summary>
    public IReadOnlyList<AccelerationSample?>? Accelerations
    {
        get => (IReadOnlyList<AccelerationSample?>?)GetValue(AccelerationsProperty);
        set => SetValue(AccelerationsProperty, value);
    }

    /// <summary>Рисовать вектор ускорения в текущей точке.</summary>
    public bool ShowAcceleration
    {
        get => (bool)GetValue(ShowAccelerationProperty);
        set => SetValue(ShowAccelerationProperty, value);
    }

    /// <summary>Срабатывает при клике рядом с точкой трека (без перетаскивания).</summary>
    public event Action<int>? TrackPointClicked;

    // ---------- правка границ ----------

    /// <summary>Сторона коридора, к которой относится вершина.</summary>
    private enum BoundarySide
    {
        None,
        Left,
        Right,
    }

    // состояние перетаскивания, наведения и выделения вершин
    private BoundarySide _hoverBoundarySide;
    private int _hoverVertexIndex = -1;
    private BoundarySide _selectedBoundarySide;
    private int _selectedVertexIndex = -1;
    private BoundarySide _dragBoundarySide;
    private int _dragVertexIndex = -1;
    private bool _dragChanged;
    private bool _lastClickAddedDraftVertex;

    // ---------- геометрия / состояние ----------

    private const double MinZoom = 8.0;
    private const double MaxZoom = 21.0;
    private const int MaxTileZoom = 19; // предел масштаба тайлов OpenStreetMap
    private const double EarthCircumference = 40075017.0; // длина экватора, м (Web Mercator)

    private static readonly double[] Steps =
    {
        0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005,
        0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10,
    };

    private double[] _unitX = Array.Empty<double>();
    private double[] _unitY = Array.Empty<double>();

    private double _centerX;   // центр вида в долях мира [0..1]
    private double _centerY;
    private double _zoom = 15.0;
    private bool _pendingFit = true;
    private Point? _dragLast;
    private Point? _pressPoint;
    private bool _dragMoved;

    // состояние размещения отрезка «начало круга»
    private (double Latitude, double Longitude)? _placementStart;
    private Point? _placementCursor;

    // черновик границы трассы: вершины и позиция курсора
    private List<GeoVertex>? _boundaryDraft;
    private Point? _boundaryCursor;

    // кэш перьев для индивидуальных цветов кругов (только поток рендеринга)
    private readonly Dictionary<Color, Pen> _coloredPens = new();

    private readonly TileStore _osmTiles = new(
        (z, x, y) => FormattableString.Invariant($"https://tile.openstreetmap.org/{z}/{x}/{y}.png"),
        TileCache.OsmDirectory,
        ".png");

    private readonly TileStore _satelliteTiles = new(
        (z, x, y) => FormattableString.Invariant(
            $"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"),
        TileCache.SatelliteDirectory,
        ".jpg");

    private bool _downloadsPaused;

    // ---------- кисти / перья (замороженные, общие) ----------

    private static readonly Typeface TextTypeface = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Pen TrackCasingPen;
    private static readonly Pen TrackPen;
    private static readonly Pen DonePen;
    private static readonly Pen GraticulePen;
    private static readonly Pen ScalePen;
    private static readonly Pen MarkerBorderPen;
    private static readonly Brush BackgroundBrush;
    private static readonly Brush GraticuleTextBrush;
    private static readonly Brush StartBrush;
    private static readonly Brush FinishBrush;
    private static readonly Brush CurrentBrush;
    private static readonly Brush CurrentHaloBrush;
    private static readonly Brush ChipBackgroundBrush;
    private static readonly Brush LapBoundaryBrush;
    private static readonly Pen LapLineCasingPen;
    private static readonly Pen LapLinePen;
    private static readonly Pen PlacementPreviewPen;
    private static readonly Pen SectorLineCasingPen;
    private static readonly Pen SectorLinePen;
    private static readonly Pen SectorPreviewPen;
    private static readonly Brush SectorBoundaryBrush;
    private static readonly Brush ZoneFillBrush;
    private static readonly Brush ZoneLeftBrush;
    private static readonly Brush ZoneRightBrush;
    private static readonly Pen ZoneLeftPen;
    private static readonly Pen ZoneRightPen;
    private static readonly Pen ZoneLeftPreviewPen;
    private static readonly Pen ZoneRightPreviewPen;
    private static readonly Pen OutsideZonePen;
    private static readonly Pen VertexHoverPen;
    private static readonly Pen VertexSelectedPen;
    private static readonly Pen AccelerationCasingPen;
    private static readonly Pen AccelerationPen;
    private static readonly Brush AccelerationBrush;

    static TrackMapControl()
    {
        TrackCasingPen = Freeze(new Pen(Brushes.White, 5.5));
        TrackPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0))), 3));
        DonePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x6C, 0x00))), 4));
        GraticulePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0))), 1));
        ScalePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0))), 1.5));
        MarkerBorderPen = Freeze(new Pen(Brushes.White, 2));

        BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF0, 0xEA)));
        GraticuleTextBrush = Freeze(new SolidColorBrush(Color.FromArgb(160, 0x40, 0x40, 0x40)));
        StartBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)));
        FinishBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)));
        CurrentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD8, 0x1B, 0x60)));
        CurrentHaloBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, 0xD8, 0x1B, 0x60)));
        ChipBackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(170, 0x20, 0x20, 0x20)));
        LapBoundaryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F)));

        LapLineCasingPen = Freeze(new Pen(Brushes.White, 6));
        LapLinePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F))), 3.5));
        PlacementPreviewPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F))), 2.5)
        {
            DashStyle = DashStyles.Dash,
        });

        var sectorColor = Color.FromRgb(0x6A, 0x1B, 0x9A);
        SectorBoundaryBrush = Freeze(new SolidColorBrush(sectorColor));
        SectorLineCasingPen = Freeze(new Pen(Brushes.White, 5.5));
        SectorLinePen = Freeze(new Pen(SectorBoundaryBrush, 3));
        SectorPreviewPen = Freeze(new Pen(SectorBoundaryBrush, 2.5)
        {
            DashStyle = DashStyles.Dash,
        });

        ZoneLeftBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x89, 0x7B)));
        ZoneRightBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00)));
        ZoneFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(28, 0x00, 0x89, 0x7B)));
        ZoneLeftPen = Freeze(new Pen(ZoneLeftBrush, 2.5));
        ZoneRightPen = Freeze(new Pen(ZoneRightBrush, 2.5));
        ZoneLeftPreviewPen = Freeze(new Pen(ZoneLeftBrush, 2.5) { DashStyle = DashStyles.Dash });
        ZoneRightPreviewPen = Freeze(new Pen(ZoneRightBrush, 2.5) { DashStyle = DashStyles.Dash });
        OutsideZonePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E))), 3));
        VertexHoverPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x37, 0x47, 0x4F))), 1.5));
        VertexSelectedPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xD8, 0x1B, 0x60))), 2));

        AccelerationBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x89, 0x7B)));
        AccelerationCasingPen = Freeze(new Pen(Brushes.White, 5));
        AccelerationPen = Freeze(new Pen(AccelerationBrush, 3));
    }

    public TrackMapControl()
    {
        // иначе тайлы рисуются за пределами элемента и перекрывают соседние панели окна
        ClipToBounds = true;

        // вид по умолчанию — район заезда из LogFile.csv
        _centerX = UnitX(43.6021);
        _centerY = UnitY(56.1196);

        _osmTiles.TileLoaded += () => Dispatcher.BeginInvoke(new Action(InvalidateVisual));
        _satelliteTiles.TileLoaded += () => Dispatcher.BeginInvoke(new Action(InvalidateVisual));
    }

    private static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>Перо заданного цвета (толщина круга фиксирована), с кэшированием.</summary>
    private Pen GetColoredPen(Color color)
    {
        if (!_coloredPens.TryGetValue(color, out var pen))
        {
            pen = new Pen(new SolidColorBrush(color), 4);
            pen.Freeze();
            _coloredPens[color] = pen;
        }

        return pen;
    }

    // ---------- координаты ----------

    private static double UnitX(double longitude) => (longitude + 180.0) / 360.0;

    private static double UnitY(double latitude)
    {
        latitude = Math.Clamp(latitude, -85.05112878, 85.05112878);
        double s = Math.Sin(latitude * Math.PI / 180.0);
        return 0.5 - Math.Log((1.0 + s) / (1.0 - s)) / (4.0 * Math.PI);
    }

    private static double LongitudeFromUnitX(double x) => x * 360.0 - 180.0;

    private static double LatitudeFromUnitY(double y)
    {
        double n = Math.PI * (1.0 - 2.0 * y);
        return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
    }

    /// <summary>Размер мира в пикселях (допускает дробный зум).</summary>
    private double WorldSize => 256.0 * Math.Pow(2.0, _zoom);

    private Point UnitToScreen(double x, double y)
    {
        double w = WorldSize;
        return new Point(
            (x - _centerX) * w + ActualWidth / 2.0,
            (y - _centerY) * w + ActualHeight / 2.0);
    }

    private Point ScreenToUnit(Point p)
    {
        double w = WorldSize;
        return new Point(
            _centerX + (p.X - ActualWidth / 2.0) / w,
            _centerY + (p.Y - ActualHeight / 2.0) / w);
    }

    private int TileZoom => Math.Clamp((int)Math.Round(_zoom), 0, MaxTileZoom);

    private Point GeoToScreen(double latitude, double longitude) =>
        UnitToScreen(UnitX(longitude), UnitY(latitude));

    private (double Latitude, double Longitude) ScreenToGeo(Point p)
    {
        var unit = ScreenToUnit(p);
        return (LatitudeFromUnitY(unit.Y), LongitudeFromUnitX(unit.X));
    }

    private static void OnPlacementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TrackMapControl)d;
        control._placementStart = null;
        control._placementCursor = null;
        control.Cursor = control.IsPlacingLapLine || control.IsPlacingSectorLine ||
                          control.IsPlacingLeftBoundary || control.IsPlacingRightBoundary
            ? Cursors.Cross
            : Cursors.Arrow;

        // вне режима границ незавершённый черновик не нужен
        if (!control.IsPlacingLeftBoundary && !control.IsPlacingRightBoundary)
        {
            control._boundaryDraft = null;
            control._boundaryCursor = null;
        }

        control.InvalidateVisual();
    }

    /// <summary>Границы пришли извне: выделение и наведение могли стать неверными.</summary>
    private static void OnBoundaryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TrackMapControl)d;
        control._selectedBoundarySide = BoundarySide.None;
        control._selectedVertexIndex = -1;
        control._hoverBoundarySide = BoundarySide.None;
        control._hoverVertexIndex = -1;
        control.InvalidateVisual();
    }

    // ---------- установка трека ----------

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TrackMapControl)d).SetTrack();

    private static void OnShowTilesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TrackMapControl)d).UpdateTileDownloadState();

    private void UpdateTileDownloadState()
    {
        bool enabled = ShowTiles && !_downloadsPaused;
        _osmTiles.DownloadEnabled = enabled;
        _satelliteTiles.DownloadEnabled = enabled;
    }

    /// <summary>Останавливает сетевые загрузки на время экспорта или импорта кэша.</summary>
    public void SetTileDownloadsPaused(bool paused)
    {
        _downloadsPaused = paused;
        UpdateTileDownloadState();
    }

    /// <summary>Забывает загруженные тайлы, чтобы они снова прочитались с диска.</summary>
    public void ReloadTiles()
    {
        _osmTiles.ClearMemory();
        _satelliteTiles.ClearMemory();
        InvalidateVisual();
    }

    private void SetTrack()
    {
        var track = Track;
        if (track is null || track.Count == 0)
        {
            _unitX = Array.Empty<double>();
            _unitY = Array.Empty<double>();
            _pendingFit = false;
            InvalidateVisual();
            return;
        }

        _unitX = new double[track.Count];
        _unitY = new double[track.Count];

        for (int i = 0; i < track.Count; i++)
        {
            _unitX[i] = UnitX(track[i].Longitude);
            _unitY[i] = UnitY(track[i].Latitude);
        }

        _pendingFit = true;
        InvalidateVisual();
    }

    /// <summary>Вписывает весь трек в экран с запасом 10%.</summary>
    public void FitToTrack()
    {
        if (_unitX.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        for (int i = 0; i < _unitX.Length; i++)
        {
            if (_unitX[i] < minX) minX = _unitX[i];
            if (_unitX[i] > maxX) maxX = _unitX[i];
            if (_unitY[i] < minY) minY = _unitY[i];
            if (_unitY[i] > maxY) maxY = _unitY[i];
        }

        double spanX = Math.Max(maxX - minX, 1e-9);
        double spanY = Math.Max(maxY - minY, 1e-9);

        double zoomX = Math.Log2((ActualWidth * 0.9) / (spanX * 256.0));
        double zoomY = Math.Log2((ActualHeight * 0.9) / (spanY * 256.0));

        _zoom = Math.Clamp(Math.Min(zoomX, zoomY), MinZoom, MaxZoom);
        _centerX = (minX + maxX) / 2.0;
        _centerY = (minY + maxY) / 2.0;
        _pendingFit = false;
        InvalidateVisual();
    }

    // ---------- отрисовка ----------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        if (_pendingFit && _unitX.Length > 0)
            FitToTrack();

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));
        DrawGraticule(dc, w, h);
        DrawTiles(dc, w, h);
        DrawZoneFill(dc);
        DrawTrack(dc);
        DrawMarkers(dc);
        DrawAcceleration(dc);
        DrawLapLine(dc);
        DrawSectorLines(dc);
        DrawZoneBoundaries(dc);
        DrawPlacementPreview(dc);
        DrawScaleBar(dc, w, h);
        DrawAttribution(dc, w, h);
        DrawPlacementHint(dc, w, h);
    }

    /// <summary>Координатная сетка — фон и основа, когда тайлы ещё не загружены.</summary>
    private void DrawGraticule(DrawingContext dc, double w, double h)
    {
        var topLeft = ScreenToUnit(new Point(0, 0));
        var bottomRight = ScreenToUnit(new Point(w, h));

        double minLon = LongitudeFromUnitX(topLeft.X);
        double maxLon = LongitudeFromUnitX(bottomRight.X);
        double minLat = LatitudeFromUnitY(bottomRight.Y);
        double maxLat = LatitudeFromUnitY(topLeft.Y);

        if (maxLon - minLon > 720 || maxLat - minLat > 170)
            return;

        double lonStep = PickStep(s => (s / 360.0) * WorldSize);
        double midLat = Math.Clamp((minLat + maxLat) / 2.0, -84.0, 84.0);
        double midUnit = UnitY(midLat);
        double latStep = PickStep(s => Math.Abs(UnitY(midLat + s) - midUnit) * WorldSize);

        int guard = 0;
        double lonStart = Math.Ceiling(minLon / lonStep) * lonStep;
        for (double lon = lonStart; lon <= maxLon && guard++ < 400; lon += lonStep)
        {
            double x = UnitToScreen(UnitX(lon), 0).X;
            if (x < -1 || x > w + 1)
                continue;

            dc.DrawLine(GraticulePen, new Point(x, 0), new Point(x, h));
            dc.DrawText(
                MakeText(lon.ToString(lonStep < 0.01 ? "0.#####" : "0.###", CultureInfo.CurrentCulture) + "°", 10, GraticuleTextBrush),
                new Point(x + 3, 2));
        }

        guard = 0;
        double latStart = Math.Ceiling(minLat / latStep) * latStep;
        for (double lat = latStart; lat <= maxLat && guard++ < 400; lat += latStep)
        {
            double y = UnitToScreen(0, UnitY(lat)).Y;
            if (y < -1 || y > h + 1)
                continue;

            dc.DrawLine(GraticulePen, new Point(0, y), new Point(w, y));
            dc.DrawText(
                MakeText(lat.ToString(latStep < 0.01 ? "0.#####" : "0.###", CultureInfo.CurrentCulture) + "°", 10, GraticuleTextBrush),
                new Point(3, y + 3));
        }
    }

    private static double PickStep(Func<double, double> pixelLength)
    {
        foreach (var step in Steps)
        {
            if (pixelLength(step) >= 70)
                return step;
        }

        return Steps[^1];
    }

    private void DrawTiles(DrawingContext dc, double w, double h)
    {
        if (!ShowTiles)
            return;

        int z = TileZoom;
        int count = 1 << z;
        var tiles = ShowSatellite ? _satelliteTiles : _osmTiles;

        var topLeft = ScreenToUnit(new Point(0, 0));
        var bottomRight = ScreenToUnit(new Point(w, h));

        int x0 = Math.Max(0, (int)Math.Floor(topLeft.X * count));
        int x1 = Math.Min(count - 1, (int)Math.Floor(bottomRight.X * count));
        int y0 = Math.Max(0, (int)Math.Floor(topLeft.Y * count));
        int y1 = Math.Min(count - 1, (int)Math.Floor(bottomRight.Y * count));

        for (int ty = y0; ty <= y1; ty++)
        {
            for (int tx = x0; tx <= x1; tx++)
            {
                var rect = TileScreenRect(z, tx, ty);
                var image = tiles.Get(z, tx, ty);

                if (image is not null)
                    dc.DrawImage(image, rect);
                else
                    tiles.Request(z, tx, ty);
            }
        }
    }

    private Rect TileScreenRect(int z, int x, int y)
    {
        double n = 1 << z;
        var p0 = UnitToScreen(x / n, y / n);
        var p1 = UnitToScreen((x + 1) / n, (y + 1) / n);
        return new Rect(p0, p1);
    }

    private void DrawTrack(DrawingContext dc)
    {
        if (_unitX.Length == 0)
            return;

        // лучший круг: только собранные сектора
        if (BestLapSectors is { Count: > 0 } bestLap)
        {
            DrawBestLap(dc, bestLap);
            return;
        }

        // режим просмотра одного сектора приоритетнее фильтра кругов
        if (TryGetHighlightedSector(out var selectedSector))
        {
            DrawSingleSector(dc, selectedSector);
            return;
        }

        // фильтр: только отмеченные круги
        if (HiddenLaps is { Count: > 0 })
        {
            DrawFilteredLaps(dc);
            return;
        }

        // весь трек: снаружи коридора — серым, внутри — обычным цветом
        var full = BuildTrackGeometry(0, _unitX.Length);
        dc.DrawGeometry(null, TrackCasingPen, full);
        dc.DrawGeometry(null, HasZoneMask ? OutsideZonePen : TrackPen, full);

        if (HasZoneMask)
            DrawTrackRuns(dc, 0, _unitX.Length, TrackPen);

        int done = Math.Clamp(CurrentIndex, 0, _unitX.Length - 1);
        if (done > 0)
            DrawTrackSegment(dc, 0, done + 1, DonePen);

        // сектора — цветом по номеру внутри круга, скрытые круги не рисуются
        if (Sectors is not null)
        {
            foreach (var sector in Sectors)
            {
                if (sector.EndIndex <= sector.StartIndex || IsLapHidden(sector.LapNumber))
                    continue;

                var color = ColorFromRgb(SectorPalette.ColorFor(sector.Number));
                DrawTrackSegment(dc, sector.StartIndex, sector.EndIndex + 1, GetColoredPen(color));
            }
        }

        // круги с индивидуальным цветом — поверх общего трека
        if (Laps is not null && LapColors is not null)
        {
            foreach (var lap in Laps)
            {
                if (!LapColors.TryGetValue(lap.Number, out var color))
                    continue;

                DrawTrackSegment(dc, lap.StartIndex, lap.EndIndex + 1, GetColoredPen(color));
            }
        }

        // границы (концы) кругов
        if (Laps is not null)
        {
            foreach (var lap in Laps)
            {
                int index = Math.Clamp(lap.EndIndex, 0, _unitX.Length - 1);
                var point = UnitToScreen(_unitX[index], _unitY[index]);
                dc.DrawEllipse(LapBoundaryBrush, MarkerBorderPen, point, 3.5, 3.5);
            }
        }
    }

    /// <summary>Фильтр кругов: рисуются только круги, не отмеченные как скрытые.</summary>
    private void DrawFilteredLaps(DrawingContext dc)
    {
        if (Laps is null)
            return;

        var baseColor = Color.FromRgb(0x15, 0x65, 0xC0);

        foreach (var lap in Laps)
        {
            if (IsLapHidden(lap.Number))
                continue;

            var color = LapColors is not null && LapColors.TryGetValue(lap.Number, out var custom)
                ? custom
                : baseColor;

            DrawTrackSegment(dc, lap.StartIndex, lap.EndIndex + 1, GetColoredPen(color));
        }

        // сектора видимых кругов
        if (Sectors is not null)
        {
            foreach (var sector in Sectors)
            {
                if (sector.EndIndex <= sector.StartIndex || IsLapHidden(sector.LapNumber))
                    continue;

                var color = ColorFromRgb(SectorPalette.ColorFor(sector.Number));
                DrawTrackSegment(dc, sector.StartIndex, sector.EndIndex + 1, GetColoredPen(color));
            }
        }

        foreach (var lap in Laps)
        {
            if (IsLapHidden(lap.Number))
                continue;

            int index = Math.Clamp(lap.EndIndex, 0, _unitX.Length - 1);
            var point = UnitToScreen(_unitX[index], _unitY[index]);
            dc.DrawEllipse(LapBoundaryBrush, MarkerBorderPen, point, 3.5, 3.5);
        }
    }

    /// <summary>Состав лучшего круга: выбранные сектора разных кругов и подписи С1, С2, …</summary>
    private void DrawBestLap(DrawingContext dc, IReadOnlyList<Sector> sectors)
    {
        foreach (var sector in sectors)
        {
            int start = Math.Clamp(sector.StartIndex, 0, _unitX.Length - 1);
            int end = Math.Clamp(sector.EndIndex, start, _unitX.Length - 1);

            var color = ColorFromRgb(SectorPalette.ColorFor(sector.Number));
            DrawTrackSegment(dc, start, end + 1, GetColoredPen(color));

            var startPoint = UnitToScreen(_unitX[start], _unitY[start]);
            var endPoint = UnitToScreen(_unitX[end], _unitY[end]);
            dc.DrawEllipse(SectorBoundaryBrush, MarkerBorderPen, startPoint, 3.5, 3.5);
            dc.DrawEllipse(SectorBoundaryBrush, MarkerBorderPen, endPoint, 3.5, 3.5);

            DrawChip(dc, $"С{sector.Number}", new Point(startPoint.X, startPoint.Y - 26));
        }
    }

    private bool IsLapHidden(int lapNumber) => HiddenLaps?.Contains(lapNumber) == true;

    /// <summary>Просмотр одного сектора: только его сегмент и подпись.</summary>
    private void DrawSingleSector(DrawingContext dc, Sector sector)
    {
        int start = Math.Clamp(sector.StartIndex, 0, _unitX.Length - 1);
        int end = Math.Clamp(sector.EndIndex, start, _unitX.Length - 1);

        var color = ColorFromRgb(SectorPalette.ColorFor(sector.Number));
        DrawTrackSegment(dc, start, end + 1, GetColoredPen(color));

        var startPoint = UnitToScreen(_unitX[start], _unitY[start]);
        var endPoint = UnitToScreen(_unitX[end], _unitY[end]);
        dc.DrawEllipse(SectorBoundaryBrush, MarkerBorderPen, startPoint, 4, 4);
        dc.DrawEllipse(SectorBoundaryBrush, MarkerBorderPen, endPoint, 4, 4);

        DrawChip(dc, $"Круг {sector.LapNumber} · Сектор {sector.Number}",
            new Point(startPoint.X, startPoint.Y - 34));
    }

    private bool TryGetHighlightedSector(out Sector sector)
    {
        var sectors = Sectors;
        if (sectors is not null && HighlightedSector >= 0 && HighlightedSector < sectors.Count)
        {
            sector = sectors[HighlightedSector];
            return true;
        }

        sector = null!;
        return false;
    }

    private static Color ColorFromRgb(uint rgb) =>
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>Геометрия ломаной по точкам [start; endExclusive).</summary>
    private StreamGeometry BuildTrackGeometry(int start, int endExclusive)
    {
        var geometry = new StreamGeometry();

        if (_unitX.Length == 0)
        {
            geometry.Freeze();
            return geometry;
        }

        start = Math.Clamp(start, 0, _unitX.Length - 1);
        endExclusive = Math.Clamp(endExclusive, start + 1, _unitX.Length);

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(UnitToScreen(_unitX[start], _unitY[start]), false, false);
            for (int i = start + 1; i < endExclusive; i++)
                ctx.LineTo(UnitToScreen(_unitX[i], _unitY[i]), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Маска задана и совпадает по длине с треком.</summary>
    private bool HasZoneMask => ZoneMask is { Count: > 0 } mask && mask.Count == _unitX.Length;

    private bool IsInsideZone(int index) => !HasZoneMask || ZoneMask![index];

    /// <summary>
    /// Рисует участок трека: без маски целиком, с маской — только непрерывные участки внутри коридора.
    /// </summary>
    private void DrawTrackSegment(DrawingContext dc, int start, int endExclusive, Pen pen)
    {
        if (!HasZoneMask)
        {
            var geometry = BuildTrackGeometry(start, endExclusive);
            dc.DrawGeometry(null, TrackCasingPen, geometry);
            dc.DrawGeometry(null, pen, geometry);
            return;
        }

        DrawTrackRuns(dc, start, endExclusive, pen);
    }

    private void DrawTrackRuns(DrawingContext dc, int start, int endExclusive, Pen pen)
    {
        foreach (var geometry in BuildTrackRuns(start, endExclusive))
        {
            dc.DrawGeometry(null, TrackCasingPen, geometry);
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>Непрерывные участки внутри коридора: каждый не короче двух точек.</summary>
    private List<StreamGeometry> BuildTrackRuns(int start, int endExclusive)
    {
        var runs = new List<StreamGeometry>();

        if (_unitX.Length == 0)
            return runs;

        start = Math.Clamp(start, 0, _unitX.Length - 1);
        endExclusive = Math.Clamp(endExclusive, start + 1, _unitX.Length);

        if (!HasZoneMask)
        {
            runs.Add(BuildTrackGeometry(start, endExclusive));
            return runs;
        }

        int i = start;
        while (i < endExclusive)
        {
            while (i < endExclusive && !IsInsideZone(i))
                i++;

            int runStart = i;
            while (i < endExclusive && IsInsideZone(i))
                i++;

            if (i - runStart >= 2)
                runs.Add(BuildTrackGeometry(runStart, i));
        }

        return runs;
    }

    private void DrawMarkers(DrawingContext dc)
    {
        if (_unitX.Length == 0)
            return;

        // старт (зелёный) и финиш (красный)
        dc.DrawEllipse(StartBrush, MarkerBorderPen, UnitToScreen(_unitX[0], _unitY[0]), 5, 5);
        int last = _unitX.Length - 1;
        dc.DrawEllipse(FinishBrush, MarkerBorderPen, UnitToScreen(_unitX[last], _unitY[last]), 5, 5);

        // текущая позиция по времени — с подписью Timestamp
        int index = Math.Clamp(CurrentIndex, 0, last);
        var pos = UnitToScreen(_unitX[index], _unitY[index]);

        dc.DrawEllipse(CurrentHaloBrush, null, pos, 11, 11);
        dc.DrawEllipse(CurrentBrush, MarkerBorderPen, pos, 5.5, 5.5);

        var track = Track;
        if (track is null)
            return;

        var label = MakeText(
            track[index].Timestamp.ToString("0.000", CultureInfo.InvariantCulture) + " с",
            11,
            Brushes.White);

        var chip = new Rect(pos.X + 12, pos.Y - 26, label.WidthIncludingTrailingWhitespace + 10, label.Height + 4);
        dc.DrawRectangle(ChipBackgroundBrush, null, chip);
        dc.DrawText(label, new Point(chip.X + 5, chip.Y + 2));
    }

    /// <summary>Вектор ускорения в текущей точке: направление и модуль.</summary>
    private void DrawAcceleration(DrawingContext dc)
    {
        if (!ShowAcceleration || _unitX.Length == 0)
            return;

        var samples = Accelerations;
        if (samples is null || samples.Count == 0)
            return;

        int index = Math.Clamp(CurrentIndex, 0, _unitX.Length - 1);
        if (index >= samples.Count || samples[index] is not { } sample)
            return;

        var anchor = UnitToScreen(_unitX[index], _unitY[index]);
        var label = string.Create(CultureInfo.CurrentCulture,
            $"{sample.MagnitudeG:0.00} G · {sample.BearingDeg:0}°");

        if (sample.MagnitudeG < 0.005)
        {
            DrawChip(dc, label, new Point(anchor.X + 16, anchor.Y + 18));
            return;
        }

        var direction = new Vector(sample.EastG, -sample.NorthG);
        if (direction.LengthSquared < 1e-12)
            return;

        direction.Normalize();

        double length = Math.Clamp(28 + sample.MagnitudeG * 60, 28, 120);
        var start = anchor + direction * 7;
        var end = anchor + direction * (7 + length);

        dc.DrawLine(AccelerationCasingPen, start, end);
        dc.DrawLine(AccelerationPen, start, end);

        var perpendicular = new Vector(-direction.Y, direction.X);
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(end - direction * 12 + perpendicular * 6, true, false);
            ctx.LineTo(end - direction * 12 - perpendicular * 6, true, false);
        }

        head.Freeze();
        dc.DrawGeometry(AccelerationBrush, null, head);

        DrawChip(dc, label, new Point(end.X + 12, end.Y - 8));
    }

    /// <summary>Отрезок «начало круга» с подписью.</summary>
    private void DrawLapLine(DrawingContext dc)
    {
        if (LapLine is not { } line)
            return;

        var a = GeoToScreen(line.StartLatitude, line.StartLongitude);
        var b = GeoToScreen(line.EndLatitude, line.EndLongitude);

        dc.DrawLine(LapLineCasingPen, a, b);
        dc.DrawLine(LapLinePen, a, b);
        dc.DrawEllipse(LapLinePen.Brush, MarkerBorderPen, a, 5, 5);
        dc.DrawEllipse(LapLinePen.Brush, MarkerBorderPen, b, 5, 5);

        DrawChip(dc, "Начало круга", new Point((a.X + b.X) / 2.0, Math.Min(a.Y, b.Y) - 26));
    }

    /// <summary>Линии границ секторов с подписями Г1, Г2, …</summary>
    private void DrawSectorLines(DrawingContext dc)
    {
        var lines = SectorLines;
        if (lines is null || lines.Count == 0)
            return;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var a = GeoToScreen(line.StartLatitude, line.StartLongitude);
            var b = GeoToScreen(line.EndLatitude, line.EndLongitude);

            dc.DrawLine(SectorLineCasingPen, a, b);
            dc.DrawLine(SectorLinePen, a, b);
            dc.DrawEllipse(SectorBoundaryBrush, MarkerBorderPen, a, 4.5, 4.5);
            dc.DrawEllipse(SectorBoundaryBrush, MarkerBorderPen, b, 4.5, 4.5);

            DrawChip(dc, $"Г{i + 1}", new Point((a.X + b.X) / 2.0, Math.Min(a.Y, b.Y) - 26));
        }
    }

    /// <summary>Готовый коридор из двух границ; null, если хотя бы одной нет.</summary>
    private TrackArea? GetTrackArea()
    {
        var left = LeftBoundary;
        var right = RightBoundary;
        return left is { IsValid: true } && right is { IsValid: true }
            ? new TrackArea(left, right)
            : null;
    }

    /// <summary>Заливка коридора трассы под треком.</summary>
    private void DrawZoneFill(DrawingContext dc)
    {
        var area = GetTrackArea();
        if (area is null)
            return;

        var geometry = BuildGeoGeometry(area.Polygon, closed: true);
        dc.DrawGeometry(ZoneFillBrush, null, geometry);
    }

    /// <summary>Границы трассы и незавершённый черновик.</summary>
    private void DrawZoneBoundaries(DrawingContext dc)
    {
        DrawBoundary(dc, LeftBoundary, ZoneLeftPen, "Левая", BoundarySide.Left);
        DrawBoundary(dc, RightBoundary, ZoneRightPen, "Правая", BoundarySide.Right);
        DrawBoundaryDraft(dc);
    }

    private void DrawBoundary(
        DrawingContext dc,
        TrackBoundary? boundary,
        Pen pen,
        string label,
        BoundarySide side)
    {
        if (boundary is not { IsValid: true })
            return;

        var geometry = BuildGeoGeometry(boundary.Vertices, closed: false);
        dc.DrawGeometry(null, TrackCasingPen, geometry);
        dc.DrawGeometry(null, pen, geometry);

        for (int i = 0; i < boundary.Vertices.Count; i++)
        {
            var vertex = boundary.Vertices[i];
            var point = GeoToScreen(vertex.Latitude, vertex.Longitude);
            dc.DrawEllipse(pen.Brush, MarkerBorderPen, point, 4, 4);

            if (_hoverBoundarySide == side && _hoverVertexIndex == i)
                dc.DrawEllipse(null, VertexHoverPen, point, 8, 8);

            if (_selectedBoundarySide == side && _selectedVertexIndex == i)
                dc.DrawEllipse(null, VertexSelectedPen, point, 10, 10);
        }

        var first = boundary.Vertices[0];
        var anchor = GeoToScreen(first.Latitude, first.Longitude);
        DrawChip(dc, label, new Point(anchor.X + 14, anchor.Y - 10));
    }

    /// <summary>Черновик границы: вершины и линия до курсора.</summary>
    private void DrawBoundaryDraft(DrawingContext dc)
    {
        if (_boundaryDraft is not { Count: > 0 } draft)
            return;

        bool left = IsPlacingLeftBoundary;
        if (!left && !IsPlacingRightBoundary)
            return;

        var pen = left ? ZoneLeftPreviewPen : ZoneRightPreviewPen;
        var brush = left ? ZoneLeftBrush : ZoneRightBrush;
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            var first = draft[0];
            ctx.BeginFigure(GeoToScreen(first.Latitude, first.Longitude), false, false);
            for (int i = 1; i < draft.Count; i++)
            {
                var vertex = draft[i];
                ctx.LineTo(GeoToScreen(vertex.Latitude, vertex.Longitude), true, false);
            }

            if (_boundaryCursor is { } cursor)
                ctx.LineTo(cursor, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        foreach (var vertex in draft)
        {
            var point = GeoToScreen(vertex.Latitude, vertex.Longitude);
            dc.DrawEllipse(brush, MarkerBorderPen, point, 4, 4);
        }
    }

    /// <summary>Геометрия ломаной по географическим вершинам.</summary>
    private StreamGeometry BuildGeoGeometry(IReadOnlyList<GeoVertex> vertices, bool closed)
    {
        var geometry = new StreamGeometry();

        if (vertices.Count == 0)
        {
            geometry.Freeze();
            return geometry;
        }

        using (var ctx = geometry.Open())
        {
            var first = vertices[0];
            ctx.BeginFigure(GeoToScreen(first.Latitude, first.Longitude), closed, closed);
            for (int i = 1; i < vertices.Count; i++)
            {
                var vertex = vertices[i];
                ctx.LineTo(GeoToScreen(vertex.Latitude, vertex.Longitude), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Предпросмотр отрезка: от первой точки до курсора.</summary>
    private void DrawPlacementPreview(DrawingContext dc)
    {
        if ((!IsPlacingLapLine && !IsPlacingSectorLine) || _placementStart is not { } start)
            return;

        var a = GeoToScreen(start.Latitude, start.Longitude);
        var b = _placementCursor ?? a;
        var pen = IsPlacingSectorLine ? SectorPreviewPen : PlacementPreviewPen;
        var brush = IsPlacingSectorLine ? SectorBoundaryBrush : LapLinePen.Brush;

        dc.DrawLine(pen, a, b);
        dc.DrawEllipse(brush, MarkerBorderPen, a, 5, 5);
    }

    /// <summary>Подсказка режима размещения в верхней части карты.</summary>
    private void DrawPlacementHint(DrawingContext dc, double w, double h)
    {
        string? text = null;

        if (IsPlacingLeftBoundary)
        {
            text = _boundaryDraft is null
                ? "Кликайте точки левой границы трассы. Двойной клик или Enter — завершить"
                : "Следующая точка левой границы. Двойной клик, Enter или правый клик — завершить (Esc — отмена)";
        }
        else if (IsPlacingRightBoundary)
        {
            text = _boundaryDraft is null
                ? "Кликайте точки правой границы трассы. Двойной клик или Enter — завершить"
                : "Следующая точка правой границы. Двойной клик, Enter или правый клик — завершить (Esc — отмена)";
        }
        else if (IsPlacingSectorLine)
        {
            text = _placementStart is null
                ? "Кликните первую точку границы сектора поперёк трека"
                : "Кликните вторую точку границы сектора (Esc — отмена)";
        }
        else if (IsPlacingLapLine)
        {
            text = _placementStart is null
                ? "Кликните первую точку отрезка поперёк трека"
                : "Кликните вторую точку отрезка (Esc — отмена)";
        }

        if (text is null)
            return;

        DrawChip(dc, text, new Point(w / 2.0, 8), centered: true);
    }

    /// <summary>Тёмная плашка с текстом. По умолчанию <paramref name="topLeft"/> — центр верхнего края плашки.</summary>
    private void DrawChip(DrawingContext dc, string text, Point topLeft, bool centered = false)
    {
        var formatted = MakeText(text, 11, Brushes.White);
        double width = formatted.WidthIncludingTrailingWhitespace;

        var chip = centered
            ? new Rect(topLeft.X - width / 2.0 - 6, topLeft.Y, width + 12, formatted.Height + 6)
            : new Rect(topLeft.X - width / 2.0 - 5, topLeft.Y, width + 10, formatted.Height + 4);

        dc.DrawRectangle(ChipBackgroundBrush, null, chip);
        dc.DrawText(formatted, new Point(chip.X + (centered ? 6 : 5), chip.Y + (centered ? 3 : 2)));
    }

    private void DrawScaleBar(DrawingContext dc, double w, double h)
    {
        double latitude = LatitudeFromUnitY(_centerY);
        double metersPerWorldWidth = EarthCircumference * Math.Cos(latitude * Math.PI / 180.0);
        if (metersPerWorldWidth <= 0)
            return;

        double pxPerMeter = WorldSize / metersPerWorldWidth;
        if (!double.IsFinite(pxPerMeter) || pxPerMeter <= 0)
            return;

        double[] steps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
        double chosen = steps[0];
        foreach (var s in steps)
        {
            if (s * pxPerMeter <= 160)
                chosen = s;
        }

        double length = chosen * pxPerMeter;
        double y = h - 16;
        double x = 14;

        dc.DrawLine(ScalePen, new Point(x, y), new Point(x + length, y));
        dc.DrawLine(ScalePen, new Point(x, y - 4), new Point(x, y + 4));
        dc.DrawLine(ScalePen, new Point(x + length, y - 4), new Point(x + length, y + 4));

        var text = chosen >= 1000
            ? (chosen / 1000).ToString("0.#", CultureInfo.CurrentCulture) + " км"
            : chosen.ToString("0", CultureInfo.CurrentCulture) + " м";

        dc.DrawText(MakeText(text, 11, ScalePen.Brush), new Point(x + 4, y - 18));
    }

    private void DrawAttribution(DrawingContext dc, double w, double h)
    {
        if (!ShowTiles)
            return;

        string attribution = ShowSatellite
            ? "© Esri, Maxar, Earthstar Geographics"
            : "© Участники OpenStreetMap";
        var text = MakeText(attribution, 10, Brushes.White);
        var bg = new Rect(w - text.WidthIncludingTrailingWhitespace - 14, h - text.Height - 6,
            text.WidthIncludingTrailingWhitespace + 8, text.Height + 4);

        dc.DrawRectangle(ChipBackgroundBrush, null, bg);
        dc.DrawText(text, new Point(bg.X + 4, bg.Y + 2));
    }

    private FormattedText MakeText(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextTypeface, size, brush,
            IsLoaded ? VisualTreeHelper.GetDpi(this).PixelsPerDip : 1.0);

    /// <summary>Ближайшая точка трека в радиусе 14 px; -1 — рядом ничего нет.</summary>
    private int HitTestTrackPoint(Point position)
    {
        if (_unitX.Length == 0)
            return -1;

        const double radius = 14.0;
        double bestDistance = radius * radius;
        int bestIndex = -1;

        for (int i = 0; i < _unitX.Length; i++)
        {
            var screen = UnitToScreen(_unitX[i], _unitY[i]);
            double dx = screen.X - position.X;
            double dy = screen.Y - position.Y;
            double distance = dx * dx + dy * dy;

            if (distance <= bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    // ---------- мышь ----------

    /// <summary>Отменяет незавершённую границу, не выключая режим ввода.</summary>
    public void CancelBoundaryDraft()
    {
        _boundaryDraft = null;
        _boundaryCursor = null;
        _lastClickAddedDraftVertex = false;
        InvalidateVisual();
    }

    /// <summary>Завершает текущую границу, если в ней уже есть две вершины.</summary>
    public void CompleteBoundaryDraft()
    {
        _lastClickAddedDraftVertex = false;

        if (_boundaryDraft is not { Count: >= 2 } draft)
            return;

        var boundary = new TrackBoundary(draft.ToArray());
        _boundaryDraft = null;
        _boundaryCursor = null;
        InvalidateVisual();

        if (IsPlacingLeftBoundary)
            LeftBoundaryCompleted?.Invoke(boundary);
        else if (IsPlacingRightBoundary)
            RightBoundaryCompleted?.Invoke(boundary);
    }

    // ---------- правка вершин границ ----------

    /// <summary>Есть ли выделенная вершина, которую можно удалить.</summary>
    public bool HasSelectedBoundaryVertex => _selectedBoundarySide != BoundarySide.None;

    /// <summary>Снимает выделение вершины.</summary>
    public void ClearBoundarySelection()
    {
        if (_selectedBoundarySide == BoundarySide.None && _selectedVertexIndex < 0)
            return;

        _selectedBoundarySide = BoundarySide.None;
        _selectedVertexIndex = -1;
        InvalidateVisual();
    }

    /// <summary>
    /// Удаляет выделенную вершину. Возвращает false, если выделения нет
    /// или в границе осталось бы меньше двух вершин.
    /// </summary>
    public bool DeleteSelectedVertex()
    {
        if (_selectedBoundarySide == BoundarySide.None || _selectedVertexIndex < 0)
            return false;

        var boundary = GetBoundary(_selectedBoundarySide);
        if (boundary is not { IsValid: true } || boundary.Vertices.Count <= 2 ||
            _selectedVertexIndex >= boundary.Vertices.Count)
        {
            return false;
        }

        var side = _selectedBoundarySide;
        var vertices = boundary.Vertices.ToList();
        vertices.RemoveAt(_selectedVertexIndex);

        CommitBoundary(side, new TrackBoundary(vertices));
        return true;
    }

    private TrackBoundary? GetBoundary(BoundarySide side) => side switch
    {
        BoundarySide.Left => LeftBoundary,
        BoundarySide.Right => RightBoundary,
        _ => null,
    };

    private void SetBoundary(BoundarySide side, TrackBoundary? boundary)
    {
        if (side == BoundarySide.Left)
            LeftBoundary = boundary;
        else if (side == BoundarySide.Right)
            RightBoundary = boundary;
    }

    /// <summary>Публикует изменённую границу и сообщает окну о правке.</summary>
    private void CommitBoundary(BoundarySide side, TrackBoundary boundary)
    {
        SetBoundary(side, boundary);
        ClearBoundarySelection();
        InvalidateVisual();

        if (side == BoundarySide.Left)
            LeftBoundaryEdited?.Invoke(boundary);
        else if (side == BoundarySide.Right)
            RightBoundaryEdited?.Invoke(boundary);
    }

    /// <summary>Ближайшая вершина готовой границы в радиусе 9 px.</summary>
    private (BoundarySide Side, int Index) HitTestBoundaryVertex(Point position)
    {
        const double radius = 9.0;
        double bestDistance = radius * radius;
        var bestSide = BoundarySide.None;
        int bestIndex = -1;

        for (int side = 0; side < 2; side++)
        {
            var current = side == 0 ? BoundarySide.Left : BoundarySide.Right;
            var boundary = GetBoundary(current);
            if (boundary is not { IsValid: true })
                continue;

            for (int i = 0; i < boundary.Vertices.Count; i++)
            {
                var vertex = boundary.Vertices[i];
                var screen = GeoToScreen(vertex.Latitude, vertex.Longitude);
                double dx = screen.X - position.X;
                double dy = screen.Y - position.Y;
                double distance = dx * dx + dy * dy;

                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    bestSide = current;
                    bestIndex = i;
                }
            }
        }

        return (bestSide, bestIndex);
    }

    /// <summary>
    /// Ближайший отрезок готовой границы в радиусе 6 px.
    /// Возвращает индекс начала отрезка и точку проекции на экране.
    /// </summary>
    private bool TryHitBoundarySegment(
        Point position,
        out BoundarySide side,
        out int segmentIndex,
        out Point projected)
    {
        const double radius = 6.0;
        const double endMargin = 0.02; // не вставляем точку вплотную к вершине
        double bestDistance = radius * radius;
        bool found = false;
        side = BoundarySide.None;
        segmentIndex = -1;
        projected = position;

        for (int s = 0; s < 2; s++)
        {
            var current = s == 0 ? BoundarySide.Left : BoundarySide.Right;
            var boundary = GetBoundary(current);
            if (boundary is not { IsValid: true })
                continue;

            var vertices = boundary.Vertices;
            for (int i = 0; i + 1 < vertices.Count; i++)
            {
                var a = GeoToScreen(vertices[i].Latitude, vertices[i].Longitude);
                var b = GeoToScreen(vertices[i + 1].Latitude, vertices[i + 1].Longitude);
                double ax = b.X - a.X;
                double ay = b.Y - a.Y;
                double lengthSquared = ax * ax + ay * ay;
                if (lengthSquared < 1e-6)
                    continue;

                double t = ((position.X - a.X) * ax + (position.Y - a.Y) * ay) / lengthSquared;
                if (t < endMargin || t > 1.0 - endMargin)
                    continue;

                var point = new Point(a.X + ax * t, a.Y + ay * t);
                double dx = point.X - position.X;
                double dy = point.Y - position.Y;
                double distance = dx * dx + dy * dy;

                if (distance > bestDistance)
                    continue;

                bestDistance = distance;
                side = current;
                segmentIndex = i;
                projected = point;
                found = true;
            }
        }

        return found;
    }

    private void UpdateBoundaryHover(Point position, bool updateCursor)
    {
        var hit = HitTestBoundaryVertex(position);

        if (hit.Side != _hoverBoundarySide || hit.Index != _hoverVertexIndex)
        {
            _hoverBoundarySide = hit.Side;
            _hoverVertexIndex = hit.Index;
            InvalidateVisual();
        }

        if (updateCursor)
            Cursor = hit.Side != BoundarySide.None ? Cursors.SizeAll : Cursors.Arrow;
    }

    /// <summary>Переносит вершину под курсор без пересчёта кругов.</summary>
    private void MoveDraggedVertex(Point position)
    {
        var boundary = GetBoundary(_dragBoundarySide);
        if (boundary is not { IsValid: true } ||
            _dragVertexIndex < 0 || _dragVertexIndex >= boundary.Vertices.Count)
        {
            return;
        }

        var geo = ScreenToGeo(position);
        var vertices = boundary.Vertices.ToArray();
        vertices[_dragVertexIndex] = new GeoVertex(geo.Latitude, geo.Longitude);

        SetBoundary(_dragBoundarySide, new TrackBoundary(vertices));
        _dragChanged = true;
        InvalidateVisual();
    }

    /// <summary>Вставляет вершину после отрезка, начинающегося с <paramref name="segmentIndex"/>.</summary>
    private void InsertBoundaryVertex(BoundarySide side, int segmentIndex, GeoVertex vertex)
    {
        var boundary = GetBoundary(side);
        if (boundary is not { IsValid: true } ||
            segmentIndex < 0 || segmentIndex >= boundary.Vertices.Count - 1)
        {
            return;
        }

        var vertices = boundary.Vertices.ToList();
        vertices.Insert(segmentIndex + 1, vertex);

        CommitBoundary(side, new TrackBoundary(vertices));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (IsPlacingLapLine || IsPlacingSectorLine)
        {
            _pressPoint = null;
            _dragMoved = false;
            var pos = e.GetPosition(this);
            var geo = ScreenToGeo(pos);

            if (_placementStart is null)
            {
                _placementStart = geo;
                _placementCursor = pos;
            }
            else
            {
                var line = new LapLine(
                    _placementStart.Value.Latitude,
                    _placementStart.Value.Longitude,
                    geo.Latitude,
                    geo.Longitude);

                _placementStart = null;
                _placementCursor = null;

                if (IsPlacingSectorLine)
                    SectorLineCompleted?.Invoke(line);
                else
                    LapLineCompleted?.Invoke(line);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var click = e.GetPosition(this);
        var vertex = HitTestBoundaryVertex(click);

        // вершина важнее добавления точки и панорамирования
        if (vertex.Side != BoundarySide.None)
        {
            _pressPoint = null;
            _dragMoved = false;
            _lastClickAddedDraftVertex = false;
            _dragBoundarySide = vertex.Side;
            _dragVertexIndex = vertex.Index;
            _dragChanged = false;
            _selectedBoundarySide = vertex.Side;
            _selectedVertexIndex = vertex.Index;

            CaptureMouse();
            Cursor = Cursors.SizeAll;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (IsPlacingLeftBoundary || IsPlacingRightBoundary)
        {
            _pressPoint = null;
            _dragMoved = false;

            // двойной клик: вставка точки в готовую границу или завершение черновика
            if (e.ClickCount >= 2)
            {
                if (e.ClickCount == 2 &&
                    TryHitBoundarySegment(click, out var side, out var segment, out var projected))
                {
                    // первый клик пары успел добавить вершину черновика — убираем её
                    if (_lastClickAddedDraftVertex && _boundaryDraft is { Count: > 0 } draft)
                        draft.RemoveAt(draft.Count - 1);

                    _lastClickAddedDraftVertex = false;

                    var insert = ScreenToGeo(projected);
                    InsertBoundaryVertex(side, segment, new GeoVertex(insert.Latitude, insert.Longitude));
                    e.Handled = true;
                    return;
                }

                CompleteBoundaryDraft();
                e.Handled = true;
                return;
            }

            var geo = ScreenToGeo(click);
            _boundaryDraft ??= new List<GeoVertex>();
            _boundaryDraft.Add(new GeoVertex(geo.Latitude, geo.Longitude));
            _lastClickAddedDraftVertex = true;
            ClearBoundarySelection();
            _boundaryCursor = click;

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // двойной клик по отрезку готовой границы вставляет вершину
        if (e.ClickCount == 2 &&
            TryHitBoundarySegment(click, out var segSide, out var segIndex, out var segPoint))
        {
            var insert = ScreenToGeo(segPoint);
            InsertBoundaryVertex(segSide, segIndex, new GeoVertex(insert.Latitude, insert.Longitude));
            e.Handled = true;
            return;
        }

        ClearBoundarySelection();
        _dragLast = click;
        _pressPoint = click;
        _dragMoved = false;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        if ((IsPlacingLeftBoundary || IsPlacingRightBoundary) && _boundaryDraft is { Count: >= 2 })
        {
            CompleteBoundaryDraft();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (IsPlacingLapLine || IsPlacingSectorLine)
        {
            if (_placementStart is not null)
            {
                _placementCursor = e.GetPosition(this);
                InvalidateVisual();
            }

            return;
        }

        if (_dragBoundarySide != BoundarySide.None)
        {
            MoveDraggedVertex(e.GetPosition(this));
            return;
        }

        if (IsPlacingLeftBoundary || IsPlacingRightBoundary)
        {
            UpdateBoundaryHover(e.GetPosition(this), updateCursor: false);

            if (_boundaryDraft is not null)
            {
                _boundaryCursor = e.GetPosition(this);
                InvalidateVisual();
            }

            return;
        }

        UpdateBoundaryHover(e.GetPosition(this), updateCursor: true);

        if (_dragLast is not { } last || !IsMouseCaptured)
            return;

        var pos = e.GetPosition(this);
        if (_pressPoint is { } press && (pos - press).Length > 4)
            _dragMoved = true;

        var delta = pos - last;
        if (delta.X == 0 && delta.Y == 0)
            return;

        double w = WorldSize;
        _centerX -= delta.X / w;
        _centerY -= delta.Y / w;
        _dragLast = pos;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // окончание перетаскивания вершины
        if (_dragBoundarySide != BoundarySide.None)
        {
            var side = _dragBoundarySide;
            bool changed = _dragChanged;

            _dragBoundarySide = BoundarySide.None;
            _dragVertexIndex = -1;
            _dragChanged = false;

            if (IsMouseCaptured)
                ReleaseMouseCapture();

            Cursor = Cursors.Arrow;

            if (changed && GetBoundary(side) is { IsValid: true } boundary)
                CommitBoundary(side, boundary);
            else
                InvalidateVisual();

            e.Handled = true;
            return;
        }

        var position = e.GetPosition(this);
        bool selectPoint = !_dragMoved && _pressPoint is not null;

        if (IsMouseCaptured)
            ReleaseMouseCapture();

        _dragLast = null;
        _pressPoint = null;
        _dragMoved = false;
        Cursor = Cursors.Arrow;

        if (selectPoint)
        {
            int hit = HitTestTrackPoint(position);
            if (hit >= 0)
                TrackPointClicked?.Invoke(hit);
        }

        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // перетаскивание прервано извне: фиксируем то, что уже сдвинуто
        if (_dragBoundarySide == BoundarySide.None)
            return;

        var side = _dragBoundarySide;
        bool changed = _dragChanged;

        _dragBoundarySide = BoundarySide.None;
        _dragVertexIndex = -1;
        _dragChanged = false;
        Cursor = Cursors.Arrow;

        if (changed && GetBoundary(side) is { IsValid: true } boundary)
            CommitBoundary(side, boundary);
        else
            InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var pos = e.GetPosition(this);
        var anchor = ScreenToUnit(pos); // гео-точка под курсором при текущем зуме

        double newZoom = Math.Clamp(_zoom + e.Delta / 120.0 * 0.4, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 1e-9)
            return;

        _zoom = newZoom;

        // оставляем привязанную к курсору точку на месте
        _centerX = anchor.X - (pos.X - ActualWidth / 2.0) / WorldSize;
        _centerY = anchor.Y - (pos.Y - ActualHeight / 2.0) / WorldSize;

        InvalidateVisual();
        e.Handled = true;
    }
}