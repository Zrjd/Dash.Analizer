using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DashAnalizer.Models;
using DashAnalizer.Services;

namespace DashAnalizer;

/// <summary>
/// Логика главного окна: загрузка CSV-лога, управление воспроизведением заезда
/// (слайдер по Timestamp), вывод текущих координат и панель показателей
/// с выбором колонок файла чекбоксами.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Показатели, отмеченные по умолчанию, если пользовательский выбор ещё не сохранялся.</summary>
    private static readonly string[] DefaultIndicators =
    {
        "Speed (kph)", "RPM (rpm)", "Throttle Position (%)", "Altitude (m)",
    };

    /// <summary>Палитра индивидуальных цветов кругов для меню «Круги».</summary>
    private static readonly (string Name, uint Rgb)[] LapPalette =
    {
        ("Синий", 0x1E88E5), ("Красный", 0xE53935), ("Оранжевый", 0xFB8C00),
        ("Жёлтый", 0xF9A825), ("Зелёный", 0x43A047), ("Бирюзовый", 0x00ACC1),
        ("Фиолетовый", 0x8E24AA), ("Розовый", 0xEC407A), ("Коричневый", 0x6D4C41),
        ("Серый", 0x616161),
    };

    private readonly DispatcherTimer _playTimer;
    private readonly List<IndicatorRow> _rows = new();
    private readonly List<int> _lapCrossings = new();
    private readonly List<double> _lapDurations = new();
    private readonly List<Lap> _laps = new();
    private readonly List<double> _lapSpeeds = new();
    private readonly Dictionary<int, Color> _lapColors = new();
    private readonly HashSet<int> _excludedLaps = new();
    private readonly List<LapLine> _sectorLines = new();
    private readonly List<Sector> _sectors = new();
    private readonly HashSet<int> _hiddenLaps = new();
    private readonly Dictionary<int, int> _bestLapOverrides = new();
    private TrackBoundary? _leftBoundary;
    private TrackBoundary? _rightBoundary;
    private TrackArea? _trackArea;
    private bool[]? _zoneMask;
    private bool _zoneFilterEnabled = true;
    private AccelerationSample?[] _accelerations = Array.Empty<AccelerationSample?>();
    private TrackLog? _log;
    private LogColumn? _speedColumn;
    private BestLap? _bestLap;
    private int _highlightedSector = -1;
    private bool _showBestLapOnMap;
    private bool _syncing;
    private bool _updatingRows;

    /// <summary>Строка панели показателей: чекбокс + текущее значение + статистика.</summary>
    private sealed class IndicatorRow
    {
        public required LogColumn Column { get; init; }
        public required CheckBox Box { get; init; }
        public required TextBlock Value { get; init; }
        public required TextBlock Stats { get; init; }
    }

    public MainWindow()
    {
        InitializeComponent();

        _playTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100), // частота записи лога — 10 Гц
        };
        _playTimer.Tick += OnPlayTick;

        Loaded += (_, _) => LoadDefaultFile();
    }

    // ---------- загрузка лога ----------

    private void LoadDefaultFile()
    {
        var path = TrackLogReader.FindDefaultLogPath();
        if (path is null)
        {
            FileText.Text = "Файл LogFile.csv не найден — нажмите «Открыть CSV…»";
            return;
        }

        LoadFile(path);
    }

    private void LoadFile(string path)
    {
        try
        {
            var log = TrackLogReader.Read(path); // файл читается только на чтение
            _log = log;
            _speedColumn = log.Columns
                .FirstOrDefault(c => string.Equals(c.Name, "Speed (kph)", StringComparison.OrdinalIgnoreCase));

            _accelerations = AccelerationCalculator.Compute(log.Points, log.Columns);
            Map.Accelerations = _accelerations;

            bool hasAcceleration = _accelerations.Any(sample => sample is not null);
            AccelToggle.IsEnabled = hasAcceleration;
            MenuAccelItem.IsEnabled = hasAcceleration;

            if (!hasAcceleration)
            {
                AccelToggle.IsChecked = false;
                MenuAccelItem.IsChecked = false;
                Map.ShowAcceleration = false;
            }
            else
            {
                Map.ShowAcceleration = AccelToggle.IsChecked == true;
            }

            Map.Track = log.Points;

            _syncing = true;
            TimeSlider.Minimum = 0;
            TimeSlider.Maximum = Math.Max(0, log.Points.Count - 1);
            TimeSlider.Value = 0;
            TimeSlider.IsEnabled = true;
            _syncing = false;

            Map.CurrentIndex = 0;
            Map.FitToTrack();
            TryLoadLapLine();
            TryLoadSectorLines();
            TryLoadBoundaries();
            RebuildTrackArea();

            FileText.Text = path;
            PointsText.Text = "Точек: " + log.Points.Count.ToString("N0", CultureInfo.CurrentCulture)
                + (log.SkippedRows > 0 ? ", пропущено строк: " + log.SkippedRows : string.Empty);
            RangeText.Text = string.Create(CultureInfo.CurrentCulture,
                $"Время: {log.StartTimestamp:0.000} … {log.EndTimestamp:0.000} с");

            BuildIndicatorRows(log);
            UpdateCurrentInfo(0);

            PlayButton.IsEnabled = true;
            UpdatePlayButton(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка чтения лога",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- кнопки панели ----------

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите лог заезда",
            Filter = "CSV-файлы (*.csv)|*.csv|Все файлы (*.*)|*.*",
        };

        var current = _log?.FilePath ?? TrackLogReader.FindDefaultLogPath();
        if (current is not null && System.IO.File.Exists(current))
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(current);

        if (dialog.ShowDialog(this) == true)
            LoadFile(dialog.FileName);
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Map.FitToTrack();

    // ---------- перенос кэша карт ----------

    private async void OnExportCacheClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить кэш карт",
            Filter = "ZIP-архив (*.zip)|*.zip|Все файлы (*.*)|*.*",
            DefaultExt = ".zip",
            FileName = $"DashAnalizer-map-cache-{DateTime.Now:yyyyMMdd}.zip",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        string path = dialog.FileName;
        SetCacheBusy(true);

        try
        {
            int count = await Task.Run(() => TileCache.Export(path));

            if (count == 0)
            {
                // пустой архив не оставляем на диске
                TryDelete(path);
                MessageBox.Show(this, "Кэш карт пуст — сохранять нечего.", "Экспорт кэша",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long size = new FileInfo(path).Length;
            MessageBox.Show(this,
                $"Сохранено тайлов: {count}. Размер архива: {FormatBytes(size)}.",
                "Экспорт кэша", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            TryDelete(path);
            MessageBox.Show(this, ex.Message, "Не удалось сохранить кэш",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetCacheBusy(false);
        }
    }

    private async void OnImportCacheClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите архив кэша карт",
            Filter = "ZIP-архив (*.zip)|*.zip|Все файлы (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        SetCacheBusy(true);

        try
        {
            int count = await Task.Run(() => TileCache.Import(dialog.FileName));
            Map.ReloadTiles(); // тайлы читаются с диска заново

            MessageBox.Show(this,
                count == 0
                    ? "В архиве не найдено тайлов карт."
                    : $"Добавлено тайлов: {count}. Существующие тайлы сохранены.",
                "Импорт кэша", MessageBoxButton.OK,
                count == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось прочитать архив",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetCacheBusy(false);
        }
    }

    /// <summary>Блокирует меню кэша и сетевые загрузки на время работы с архивом.</summary>
    private void SetCacheBusy(bool busy)
    {
        ExportCacheItem.IsEnabled = !busy;
        ImportCacheItem.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;

        if (Map is not null)
            Map.SetTileDownloadsPaused(busy);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // не удалось убрать недописанный архив — не критично
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " МБ"
            : (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " КБ";

    private void OnTilesToggleChanged(object sender, RoutedEventArgs e)
    {
        // Checked срабатывает уже во время InitializeComponent — Map ещё может не существовать
        if (Map is not null)
            Map.ShowTiles = TilesToggle.IsChecked == true;

        if (MenuTilesItem is not null)
            MenuTilesItem.IsChecked = TilesToggle.IsChecked == true;
    }

    private void OnMenuTilesChanged(object sender, RoutedEventArgs e)
    {
        // Checked срабатывает при создании меню — TilesToggle ещё может не существовать
        if (TilesToggle is not null)
            TilesToggle.IsChecked = MenuTilesItem.IsChecked == true;
    }

    private void OnSatelliteToggleChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = SatelliteToggle.IsChecked == true;

        if (MenuSatelliteItem is not null)
            MenuSatelliteItem.IsChecked = enabled;

        ApplySatellite(enabled);
    }

    private void OnMenuSatelliteChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = MenuSatelliteItem.IsChecked;

        if (SatelliteToggle is not null)
            SatelliteToggle.IsChecked = enabled;

        ApplySatellite(enabled);
    }

    /// <summary>Спутник показывает карту: при включении подложка тоже включается.</summary>
    private void ApplySatellite(bool enabled)
    {
        if (enabled && TilesToggle is not null && TilesToggle.IsChecked != true)
            TilesToggle.IsChecked = true; // включит подложку через OnTilesToggleChanged

        if (Map is not null)
            Map.ShowSatellite = enabled;
    }

    private void OnAccelToggleChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = AccelToggle.IsChecked == true;

        if (Map is not null)
            Map.ShowAcceleration = enabled;

        if (MenuAccelItem is not null)
            MenuAccelItem.IsChecked = enabled;

        UpdateCurrentInfo(CurrentIndex);
    }

    private void OnMenuAccelChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = MenuAccelItem.IsChecked;

        if (AccelToggle is not null)
            AccelToggle.IsChecked = enabled;

        if (Map is not null)
            Map.ShowAcceleration = enabled;
    }

    /// <summary>Клик рядом с точкой трека: перевести на неё время и показать вектор ускорения.</summary>
    private void OnTrackPointClicked(int index)
    {
        if (_log is null || _log.Points.Count == 0)
            return;

        index = Math.Clamp(index, 0, _log.Points.Count - 1);

        _syncing = true;
        TimeSlider.Value = index;
        _syncing = false;

        Map.CurrentIndex = index;
        UpdateCurrentInfo(index);

        if (AccelToggle.IsEnabled && AccelToggle.IsChecked != true)
            AccelToggle.IsChecked = true;
    }

    // ---------- воспроизведение по времени ----------

    private void OnPlayClick(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_log is null)
            return;

        if (_playTimer.IsEnabled)
        {
            StopPlay();
            return;
        }

        if ((int)Math.Round(TimeSlider.Value) >= _log.Points.Count - 1)
            TimeSlider.Value = 0;

        _playTimer.Start();
        UpdatePlayButton(true);
    }

    private void StopPlay()
    {
        _playTimer.Stop();
        UpdatePlayButton(false);
    }

    private void UpdatePlayButton(bool playing) =>
        PlayButton.Content = playing ? "⏸ Пауза" : "▶ Пуск";

    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (_log is null)
            return;

        int next = (int)Math.Round(TimeSlider.Value) + 1;
        if (next > _log.Points.Count - 1)
        {
            StopPlay();
            return;
        }

        TimeSlider.Value = next;
    }

    private void OnTimeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _log is null)
            return;

        int index = (int)Math.Round(TimeSlider.Value);
        Map.CurrentIndex = index;
        UpdateCurrentInfo(index);
    }

    private void UpdateCurrentInfo(int index)
    {
        if (_log is null || _log.Points.Count == 0)
            return;

        index = Math.Clamp(index, 0, _log.Points.Count - 1);
        var point = _log.Points[index];

        TimeText.Text = point.Timestamp.ToString("0.000", CultureInfo.InvariantCulture) + " с";
        CurrentText.Text = string.Create(CultureInfo.CurrentCulture,
            $"Текущая точка {index + 1}/{_log.Points.Count}: " +
            $"{point.Latitude.ToString("0.#######", CultureInfo.InvariantCulture)}, " +
            $"{point.Longitude.ToString("0.#######", CultureInfo.InvariantCulture)}");

        foreach (var row in _rows)
            UpdateIndicatorRow(row, index);

        UpdateLapInfo(index);
        UpdateSectorInfo(index);
        UpdateAccelerationInfo(index);
    }

    /// <summary>Строка ускорения в статус-баре для точки <paramref name="index"/>.</summary>
    private void UpdateAccelerationInfo(int index)
    {
        if (index >= 0 && index < _accelerations.Length && _accelerations[index] is { } sample)
        {
            AccelText.Text = string.Create(CultureInfo.CurrentCulture,
                $"Ускорение: {sample.MagnitudeG:0.00} G · {sample.BearingDeg:0}°");
        }
        else
        {
            AccelText.Text = "Ускорение: —";
        }
    }

    // ---------- начало круга ----------

    private void OnSetLapLineClick(object sender, RoutedEventArgs e)
    {
        bool placing = !Map.IsPlacingLapLine;

        if (placing && Map.IsPlacingSectorLine)
        {
            Map.IsPlacingSectorLine = false;
            RebuildSectorsMenu();
        }

        Map.IsPlacingLapLine = placing;
        SetLapLineItem.IsChecked = placing;
    }

    private void OnClearLapLineClick(object sender, RoutedEventArgs e)
    {
        Map.LapLine = null;
        Map.IsPlacingLapLine = false;
        SetLapLineItem.IsChecked = false;
        ClearLapLineItem.IsEnabled = false;
        _lapColors.Clear();
        Map.LapColors = null;
        _hiddenLaps.Clear();
        Map.HiddenLaps = null;
        RecomputeLaps();
        SaveLapLine();
        UpdateCurrentInfo(CurrentIndex);
    }

    private void OnLapLineCompleted(LapLine line)
    {
        Map.LapLine = line;
        Map.IsPlacingLapLine = false;
        SetLapLineItem.IsChecked = false;
        ClearLapLineItem.IsEnabled = true;
        _lapColors.Clear(); // новая линия — новая нумерация кругов, сбрасываем цвета
        Map.LapColors = null;
        _hiddenLaps.Clear();
        Map.HiddenLaps = null;
        RecomputeLaps();
        SaveLapLine();
        UpdateCurrentInfo(CurrentIndex);
    }

    /// <summary>Обновляет строку круга в статус-баре для точки <paramref name="index"/>.</summary>
    private void UpdateLapInfo(int index)
    {
        if (Map.LapLine is null)
        {
            LapText.Text = string.Empty;
            return;
        }

        if (_lapCrossings.Count == 0)
        {
            LapText.Text = "⚠ Отрезок не пересекает трек — задайте заново";
            return;
        }

        int completed = 0;
        while (completed < _lapCrossings.Count && _lapCrossings[completed] <= index)
            completed++;

        int lapNumber = completed + 1;
        int lastBoundary = completed > 0 ? _lapCrossings[completed - 1] : 0;
        double current = _log!.Points[index].Timestamp - _log.Points[lastBoundary].Timestamp;
        double last = completed >= 1 ? _lapDurations[completed - 1] : double.NaN;

        double best = double.NaN;
        for (int i = 0; i < completed; i++)
        {
            if (double.IsNaN(best) || _lapDurations[i] < best)
                best = _lapDurations[i];
        }

        LapText.Text = string.Create(CultureInfo.CurrentCulture,
            $"Круг {lapNumber} · текущий {FormatLapTime(current)} · последний {FormatLapTime(last)} · лучший {FormatLapTime(best)}");
    }

    private static string FormatLapTime(double seconds) =>
        double.IsNaN(seconds) ? "—" : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff");

    /// <summary>Строка сектора в статус-баре для точки <paramref name="index"/>.</summary>
    private void UpdateSectorInfo(int index)
    {
        Sector? match = null;

        foreach (var sector in _sectors)
        {
            if (sector.StartIndex <= index && index <= sector.EndIndex)
                match = sector;
        }

        SectorText.Text = match is null
            ? string.Empty
            : $"Круг {match.LapNumber} · Сектор {match.Number}";
    }

    private static string LapLinePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashAnalizer",
        "lapline.txt");

    private void SaveLapLine()
    {
        try
        {
            var path = LapLinePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (Map.LapLine is { } line)
            {
                File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
                    $"{line.StartLatitude:0.#######} {line.StartLongitude:0.#######} " +
                    $"{line.EndLatitude:0.#######} {line.EndLongitude:0.#######}"));
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // сохранение метки не критично для работы приложения
        }
    }

    private void TryLoadLapLine()
    {
        try
        {
            var path = LapLinePath;
            if (!File.Exists(path))
                return;

            var parts = File.ReadAllText(path)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
                return;

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var startLat) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var startLon) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var endLat) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var endLon))
                return;

            Map.LapLine = new LapLine(startLat, startLon, endLat, endLon);
            ClearLapLineItem.IsEnabled = true;
            RecomputeLaps();
        }
        catch
        {
            // повреждённая метка игнорируется
        }
    }

    /// <summary>Пересчитывает пересечения, круги, их скорость и обновляет меню/статистику.</summary>
    private void RecomputeLaps()
    {
        _lapCrossings.Clear();
        _lapDurations.Clear();
        _laps.Clear();
        _lapSpeeds.Clear();
        _excludedLaps.Clear();
        _hiddenLaps.Clear(); // нумерация кругов зависит от линии круга
        _highlightedSector = -1; // нумерация секторов зависит от линии круга

        if (Map.LapLine is { } line && _log is not null)
        {
            var mask = _zoneMask;
            _lapCrossings.AddRange(LapCounter.FindCrossings(_log.Points, line, insideMask: mask));
            _lapDurations.AddRange(LapCounter.ComputeLapDurations(_log.Points, _lapCrossings));

            foreach (var lap in LapCounter.BuildLaps(_log.Points, _lapCrossings))
            {
                _laps.Add(lap);
                _lapSpeeds.Add(LapCounter.AverageSpeedKph(_log.Points, lap, _speedColumn, mask));
            }
        }

        // копии — чтобы DependencyProperty увидел изменение и перерисовал карту
        Map.Laps = _laps.Count > 0 ? new List<Lap>(_laps) : null;
        Map.HiddenLaps = _hiddenLaps.Count > 0 ? new HashSet<int>(_hiddenLaps) : null;

        RebuildLapsMenu();
        UpdateLapStats();
        RecomputeSectors();
    }

    /// <summary>Блок «Статистика кругов» в правой панели.</summary>
    private void UpdateLapStats()
    {
        if (_laps.Count == 0)
        {
            LapStatsBox.Visibility = Visibility.Collapsed;
            StatsText.Text = string.Empty;
            return;
        }

        LapStatsBox.Visibility = Visibility.Visible;

        var included = _laps.Where(l => !_excludedLaps.Contains(l.Number)).ToList();
        if (included.Count == 0)
        {
            StatsText.Text = string.Create(CultureInfo.CurrentCulture,
                $"Кругов завершено: {_laps.Count}\nВсе круги исключены из статистики");
            return;
        }

        double avgTime = included.Average(l => l.DurationSeconds);

        var validSpeeds = included
            .Select(l => _lapSpeeds[l.Number - 1])
            .Where(s => !double.IsNaN(s))
            .ToList();
        var avgSpeed = validSpeeds.Count > 0 ? validSpeeds.Average() : double.NaN;

        var best = included.OrderBy(l => l.DurationSeconds).First();
        var worst = included.OrderByDescending(l => l.DurationSeconds).First();

        StatsText.Text = string.Create(CultureInfo.CurrentCulture,
            $"Кругов завершено: {_laps.Count} · в статистике: {included.Count}\n" +
            $"Среднее время: {FormatLapTime(avgTime)}\n" +
            $"Средняя скорость: {(double.IsNaN(avgSpeed) ? "—" : avgSpeed.ToString("0.0", CultureInfo.CurrentCulture) + " км/ч")}\n" +
            $"Лучший: №{best.Number} — {FormatLapTime(best.DurationSeconds)}\n" +
            $"Худший: №{worst.Number} — {FormatLapTime(worst.DurationSeconds)}");
    }

    /// <summary>Перестраивает меню «Круги»: «Весь трек» + список кругов с цветом и просмотром.</summary>
    private void RebuildLapsMenu()
    {
        LapsMenu.Items.Clear();

        if (_laps.Count == 0)
        {
            LapsMenu.IsEnabled = false;
            LapsMenu.Items.Add(new MenuItem { Header = "Нет завершённых кругов", IsEnabled = false });
            return;
        }

        LapsMenu.IsEnabled = true;

        var allItem = new MenuItem
        {
            Header = "Показать все круги",
            IsCheckable = true,
            IsChecked = _hiddenLaps.Count == 0,
        };
        allItem.Click += (_, _) => ShowAllLaps();
        LapsMenu.Items.Add(allItem);
        LapsMenu.Items.Add(new Separator());

        foreach (var lap in _laps)
        {
            int index = lap.Number - 1;
            bool hasColor = _lapColors.TryGetValue(lap.Number, out var lapColor);
            bool excluded = _excludedLaps.Contains(lap.Number);

            var header = new TextBlock();
            header.Inlines.Add(hasColor
                ? new Run("■ ") { Foreground = new SolidColorBrush(lapColor) }
                : new Run("■ ") { Foreground = Brushes.Gray });
            header.Inlines.Add(new Run($"Круг {lap.Number} — {FormatLapTime(lap.DurationSeconds)}")
            {
                Foreground = excluded ? Brushes.Gray : Brushes.Black,
            });
            if (excluded)
                header.Inlines.Add(new Run(" · исключён") { Foreground = Brushes.Gray });

            var lapItem = new MenuItem { Header = header };

            var includeItem = new MenuItem
            {
                Header = "Учитывать в статистике",
                IsCheckable = true,
                IsChecked = !excluded,
                Foreground = SystemColors.ControlTextBrush,
            };
            includeItem.Click += (_, _) => SetLapExcluded(lap.Number, includeItem.IsChecked != true);
            lapItem.Items.Add(includeItem);

            var viewItem = new MenuItem
            {
                Header = "Показывать на карте",
                IsCheckable = true,
                IsChecked = !_hiddenLaps.Contains(lap.Number),
                Foreground = SystemColors.ControlTextBrush,
            };
            viewItem.Click += (_, _) => SetLapHidden(lap.Number, viewItem.IsChecked != true);
            lapItem.Items.Add(viewItem);

            var colorsItem = new MenuItem { Header = "Цвет", Foreground = SystemColors.ControlTextBrush };
            foreach (var (name, rgb) in LapPalette)
            {
                var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                var colorItem = new MenuItem
                {
                    Header = "■ " + name,
                    Foreground = new SolidColorBrush(color),
                };

                int captured = index;
                colorItem.Click += (_, _) => SetLapColor(captured, color);
                colorsItem.Items.Add(colorItem);
            }

            var resetItem = new MenuItem { Header = "Как у трека", Foreground = SystemColors.ControlTextBrush };
            resetItem.Click += (_, _) => SetLapColor(index, null);
            colorsItem.Items.Add(resetItem);
            lapItem.Items.Add(colorsItem);

            LapsMenu.Items.Add(lapItem);
        }
    }

    private void SetLapHidden(int lapNumber, bool hidden)
    {
        if (hidden)
            _hiddenLaps.Add(lapNumber);
        else
            _hiddenLaps.Remove(lapNumber);

        // скрывать все круги нельзя: фильтр сбрасывается и снова виден весь трек
        if (_laps.Count > 0 && _hiddenLaps.Count >= _laps.Count)
            _hiddenLaps.Clear();

        Map.HiddenLaps = _hiddenLaps.Count > 0 ? new HashSet<int>(_hiddenLaps) : null;

        // отложенно, чтобы не перестраивать пункты во время закрытия меню
        Dispatcher.BeginInvoke(new Action(RebuildLapsMenu), DispatcherPriority.Background);
    }

    private void ShowAllLaps()
    {
        _hiddenLaps.Clear();
        Map.HiddenLaps = null;

        Dispatcher.BeginInvoke(new Action(RebuildLapsMenu), DispatcherPriority.Background);
    }

    private void SetLapColor(int lapIndex, Color? color)
    {
        int number = lapIndex + 1;

        if (color is { } value)
            _lapColors[number] = value;
        else
            _lapColors.Remove(number);

        Map.LapColors = _lapColors.Count > 0
            ? new Dictionary<int, Color>(_lapColors)
            : null;

        Dispatcher.BeginInvoke(new Action(RebuildLapsMenu), DispatcherPriority.Background);
    }

    /// <summary>Исключает круг из статистики или возвращает его обратно. Действует до смены разметки.</summary>
    private void SetLapExcluded(int lapNumber, bool excluded)
    {
        if (excluded)
            _excludedLaps.Add(lapNumber);
        else
            _excludedLaps.Remove(lapNumber);

        UpdateLapStats();

        // отложенно, чтобы не перестраивать пункты во время закрытия меню
        Dispatcher.BeginInvoke(new Action(RebuildLapsMenu), DispatcherPriority.Background);
    }

    // ---------- сектора ----------

    private void OnSetSectorLineClick(object sender, RoutedEventArgs e)
    {
        bool placing = !Map.IsPlacingSectorLine;

        if (placing && Map.IsPlacingLapLine)
        {
            Map.IsPlacingLapLine = false;
            SetLapLineItem.IsChecked = false;
        }

        Map.IsPlacingSectorLine = placing;
        RebuildSectorsMenu();
    }

    private void OnSectorLineCompleted(LapLine line)
    {
        _sectorLines.Add(line);
        Map.SectorLines = new List<LapLine>(_sectorLines);
        RecomputeSectors();
        SaveSectorLines();
        UpdateCurrentInfo(CurrentIndex);
    }

    private void OnRemoveSectorLineClick(object sender, RoutedEventArgs e)
    {
        if (_sectorLines.Count == 0)
            return;

        _sectorLines.RemoveAt(_sectorLines.Count - 1);
        Map.SectorLines = _sectorLines.Count > 0 ? new List<LapLine>(_sectorLines) : null;
        RecomputeSectors();
        SaveSectorLines();
        UpdateCurrentInfo(CurrentIndex);
    }

    private void OnClearSectorLinesClick(object sender, RoutedEventArgs e)
    {
        _sectorLines.Clear();
        Map.SectorLines = null;
        Map.IsPlacingSectorLine = false;
        RecomputeSectors();
        SaveSectorLines();
        UpdateCurrentInfo(CurrentIndex);
    }

    /// <summary>Пересчитывает сектора по всем линиям и обновляет карту, меню и статистику.</summary>
    private void RecomputeSectors()
    {
        _sectors.Clear();

        if (_log is not null && _sectorLines.Count > 0)
        {
            var boundaries = SectorCounter.CollectBoundaries(
                _log.Points, _sectorLines, Map.LapLine, _zoneMask);
            _sectors.AddRange(SectorCounter.BuildSectors(
                _log.Points, boundaries, _lapCrossings, _zoneMask));
        }

        if (_highlightedSector >= _sectors.Count)
            _highlightedSector = -1;

        // копии — чтобы DependencyProperty увидел изменение и перерисовал карту
        Map.Sectors = _sectors.Count > 0 ? new List<Sector>(_sectors) : null;
        Map.HighlightedSector = _highlightedSector;

        // индексы секторов изменились — ручные правки лучшего круга больше не действуют
        _bestLapOverrides.Clear();
        RecomputeBestLap();

        RebuildSectorsMenu();
        UpdateSectorStats();
    }

    /// <summary>Меню «Сектора»: действия с границами и список секторов по кругам.</summary>
    private void RebuildSectorsMenu()
    {
        RemoveSectorLineItem.IsEnabled = _sectorLines.Count > 0;
        ClearSectorLinesItem.IsEnabled = _sectorLines.Count > 0;
        AddSectorLineItem.IsChecked = Map.IsPlacingSectorLine;

        SectorListMenu.Items.Clear();
        SectorListMenu.IsEnabled = _sectors.Count > 0;

        if (_sectors.Count == 0)
        {
            SectorListMenu.Items.Add(new MenuItem
            {
                Header = _sectorLines.Count == 0
                    ? "Границы секторов не заданы"
                    : "Линии не пересекают трек",
                IsEnabled = false,
            });
            return;
        }

        var allItem = new MenuItem
        {
            Header = "Весь трек",
            IsCheckable = true,
            IsChecked = _highlightedSector < 0,
        };
        allItem.Click += (_, _) => SetHighlightedSector(-1);
        SectorListMenu.Items.Add(allItem);
        SectorListMenu.Items.Add(new Separator());

        var entries = _sectors
            .Select((sector, index) => (Sector: sector, Index: index))
            .GroupBy(entry => entry.Sector.LapNumber)
            .OrderBy(group => group.Key);

        foreach (var group in entries)
        {
            var lapItem = new MenuItem { Header = $"Круг {group.Key}" };

            foreach (var entry in group.OrderBy(item => item.Sector.Number))
            {
                var sector = entry.Sector;
                var color = ColorFromRgb(SectorPalette.ColorFor(sector.Number));

                var header = new TextBlock();
                header.Inlines.Add(new Run("■ ") { Foreground = new SolidColorBrush(color) });
                header.Inlines.Add(new Run(
                    $"С{sector.Number} — {FormatLapTime(sector.DurationSeconds)} · " +
                    $"{FormatSectorDistance(sector.DistanceMeters)} · " +
                    $"{sector.SpeedKph.ToString("0.0", CultureInfo.CurrentCulture)} км/ч"));

                int captured = entry.Index;
                var item = new MenuItem
                {
                    Header = header,
                    IsCheckable = true,
                    IsChecked = entry.Index == _highlightedSector,
                };
                item.Click += (_, _) => SetHighlightedSector(captured);
                lapItem.Items.Add(item);
            }

            SectorListMenu.Items.Add(lapItem);
        }
    }

    private void SetHighlightedSector(int index)
    {
        _highlightedSector = _highlightedSector == index ? -1 : index;
        Map.HighlightedSector = _highlightedSector;

        // выбранный сектор и лучший круг не смешиваются
        if (_highlightedSector >= 0)
            SetBestLapOnMap(false);

        // отложенно, чтобы не перестраивать пункты во время закрытия меню
        Dispatcher.BeginInvoke(new Action(RebuildSectorsMenu), DispatcherPriority.Background);
    }

    // ---------- лучший круг ----------

    private void OnBestLapAutoClick(object sender, RoutedEventArgs e)
    {
        _bestLapOverrides.Clear();
        RecomputeBestLap();
    }

    private void OnBestLapShowClick(object sender, RoutedEventArgs e) =>
        SetBestLapOnMap(BestLapShowItem.IsChecked);

    private void OnBestLapShowChecked(object sender, RoutedEventArgs e) =>
        SetBestLapOnMap(BestLapShowBox.IsChecked == true);

    /// <summary>Собирает лучший круг из секторов и обновляет карту, меню и панель.</summary>
    private void RecomputeBestLap()
    {
        _bestLap = BestLapBuilder.Build(_sectors, _bestLapOverrides);

        UpdateBestLapPanel();
        Map.BestLapSectors = _showBestLapOnMap && _bestLap is not null
            ? _bestLap.Items.Select(item => item.Sector).ToList()
            : null;

        // отложенно, чтобы не перестраивать пункты во время закрытия меню
        Dispatcher.BeginInvoke(new Action(RebuildBestLapMenu), DispatcherPriority.Background);
    }

    private void SetBestLapOverride(int sectorNumber, int lapNumber)
    {
        _bestLapOverrides[sectorNumber] = lapNumber;
        RecomputeBestLap();
    }

    /// <summary>Включает изолированный показ состава лучшего круга на карте.</summary>
    private void SetBestLapOnMap(bool show)
    {
        _showBestLapOnMap = show;

        if (BestLapShowItem is not null)
            BestLapShowItem.IsChecked = show;

        if (BestLapShowBox is not null)
            BestLapShowBox.IsChecked = show;

        // лучший круг и одиночный сектор не смешиваются
        if (show && _highlightedSector >= 0)
        {
            _highlightedSector = -1;
            Map.HighlightedSector = -1;
            Dispatcher.BeginInvoke(new Action(RebuildSectorsMenu), DispatcherPriority.Background);
        }

        Map.BestLapSectors = show && _bestLap is not null
            ? _bestLap.Items.Select(item => item.Sector).ToList()
            : null;
    }

    private void UpdateBestLapPanel()
    {
        if (_bestLap is null)
        {
            BestLapBox.Visibility = Visibility.Collapsed;
            BestLapText.Text = string.Empty;
            return;
        }

        BestLapBox.Visibility = Visibility.Visible;

        var speed = _bestLap.AverageSpeedKph;
        var lines = new List<string>
        {
            $"Время: {FormatLapTime(_bestLap.TotalSeconds)} · " +
            $"Дистанция: {FormatSectorDistance(_bestLap.TotalDistanceMeters)} · " +
            "Средняя: " + (double.IsNaN(speed)
                ? "—"
                : speed.ToString("0.0", CultureInfo.CurrentCulture) + " км/ч"),
        };

        foreach (var item in _bestLap.Items)
        {
            lines.Add(
                $"С{item.Sector.Number} — Круг {item.Sector.LapNumber} · " +
                FormatLapTime(item.Sector.DurationSeconds) +
                (item.IsManual ? " (вручную)" : string.Empty));
        }

        lines.Add($"Секторов: {_bestLap.Items.Count} из {_bestLap.ExpectedCount}");
        BestLapText.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>Меню «Лучший круг»: автосбор, показ на карте и выбор сектора каждого номера.</summary>
    private void RebuildBestLapMenu()
    {
        BestLapMenu.IsEnabled = _sectors.Count > 0;
        BestLapAutoItem.IsEnabled = _bestLapOverrides.Count > 0;
        BestLapShowItem.IsEnabled = _bestLap is not null;
        BestLapShowItem.IsChecked = _showBestLapOnMap;
        BestLapShowBox.IsEnabled = _bestLap is not null;

        BestLapSummaryItem.Header = _bestLap is null
            ? "Нет данных"
            : $"Итог: {FormatLapTime(_bestLap.TotalSeconds)} · " +
              $"{FormatSectorDistance(_bestLap.TotalDistanceMeters)}";

        BestLapPickerMenu.Items.Clear();
        BestLapPickerMenu.IsEnabled = _bestLap is not null;

        if (_bestLap is null)
        {
            BestLapPickerMenu.Items.Add(new MenuItem
            {
                Header = "Сектора не заданы",
                IsEnabled = false,
            });
            return;
        }

        var chosen = _bestLap.Items.ToDictionary(item => item.Sector.Number);

        foreach (var group in _sectors.GroupBy(sector => sector.Number).OrderBy(group => group.Key))
        {
            var current = chosen[group.Key].Sector;
            var sectorMenu = new MenuItem { Header = $"С{group.Key} — Круг {current.LapNumber}" };

            foreach (var sector in group.OrderBy(item => item.LapNumber))
            {
                var item = new MenuItem
                {
                    Header = $"Круг {sector.LapNumber} — {FormatLapTime(sector.DurationSeconds)} · " +
                             $"{FormatSectorDistance(sector.DistanceMeters)} · " +
                             $"{sector.SpeedKph.ToString("0.0", CultureInfo.CurrentCulture)} км/ч",
                    IsCheckable = true,
                    IsChecked = ReferenceEquals(sector, current),
                };

                int number = group.Key;
                int lapNumber = sector.LapNumber;
                item.Click += (_, _) => SetBestLapOverride(number, lapNumber);
                sectorMenu.Items.Add(item);
            }

            BestLapPickerMenu.Items.Add(sectorMenu);
        }
    }

    /// <summary>Блок «Статистика секторов»: среднее и лучшее время по номеру сектора.</summary>
    private void UpdateSectorStats()
    {
        if (_sectors.Count == 0)
        {
            SectorStatsBox.Visibility = Visibility.Collapsed;
            SectorStatsText.Text = string.Empty;
            return;
        }

        SectorStatsBox.Visibility = Visibility.Visible;

        int lapCount = _sectors.Select(s => s.LapNumber).Distinct().Count();
        var lines = new List<string>
        {
            $"Кругов с секторами: {lapCount} · секторов: {_sectors.Count}",
        };

        foreach (var group in _sectors.GroupBy(s => s.Number).OrderBy(g => g.Key).Take(8))
        {
            double average = group.Average(s => s.DurationSeconds);
            double best = group.Min(s => s.DurationSeconds);
            lines.Add($"С{group.Key}: среднее {FormatLapTime(average)} · лучшее {FormatLapTime(best)}");
        }

        double meters = _sectors.Sum(s => s.DistanceMeters);
        double seconds = _sectors.Sum(s => s.DurationSeconds);
        double speed = seconds > 0 ? meters / seconds * 3.6 : double.NaN;
        lines.Add("Средняя скорость: " +
            (double.IsNaN(speed) ? "—" : speed.ToString("0.0", CultureInfo.CurrentCulture) + " км/ч"));

        SectorStatsText.Text = string.Join(Environment.NewLine, lines);
    }

    private static string FormatSectorDistance(double meters) =>
        meters >= 1000
            ? (meters / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " км"
            : meters.ToString("0", CultureInfo.CurrentCulture) + " м";

    private static Color ColorFromRgb(uint rgb) =>
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static string SectorLinesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashAnalizer",
        "sector-lines.txt");

    private void SaveSectorLines()
    {
        try
        {
            var path = SectorLinesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (_sectorLines.Count > 0)
            {
                File.WriteAllLines(path, _sectorLines.Select(line => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{line.StartLatitude:0.#######} {line.StartLongitude:0.#######} " +
                    $"{line.EndLatitude:0.#######} {line.EndLongitude:0.#######}")));
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // сохранение границ не критично для работы приложения
        }
    }

    private void TryLoadSectorLines()
    {
        _sectorLines.Clear();

        try
        {
            var path = SectorLinesPath;
            if (File.Exists(path))
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length != 4)
                        continue;

                    if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var startLat) ||
                        !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var startLon) ||
                        !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var endLat) ||
                        !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var endLon))
                        continue;

                    _sectorLines.Add(new LapLine(startLat, startLon, endLat, endLon));
                }
            }
        }
        catch
        {
            // повреждённые границы игнорируются
        }

        Map.SectorLines = _sectorLines.Count > 0 ? new List<LapLine>(_sectorLines) : null;
    }

    // ---------- коридор трассы ----------

    private static string LeftBoundaryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashAnalizer",
        "track-left.txt");

    private static string RightBoundaryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashAnalizer",
        "track-right.txt");

    private void OnSetLeftBoundaryClick(object sender, RoutedEventArgs e)
    {
        bool placing = !Map.IsPlacingLeftBoundary;

        if (placing)
        {
            Map.IsPlacingLapLine = false;
            SetLapLineItem.IsChecked = false;
            Map.IsPlacingSectorLine = false;
            AddSectorLineItem.IsChecked = false;
            Map.IsPlacingRightBoundary = false;
            SetRightBoundaryItem.IsChecked = false;
            Map.CancelBoundaryDraft();
        }

        Map.IsPlacingLeftBoundary = placing;
        SetLeftBoundaryItem.IsChecked = placing;
    }

    private void OnSetRightBoundaryClick(object sender, RoutedEventArgs e)
    {
        bool placing = !Map.IsPlacingRightBoundary;

        if (placing)
        {
            Map.IsPlacingLapLine = false;
            SetLapLineItem.IsChecked = false;
            Map.IsPlacingSectorLine = false;
            AddSectorLineItem.IsChecked = false;
            Map.IsPlacingLeftBoundary = false;
            SetLeftBoundaryItem.IsChecked = false;
            Map.CancelBoundaryDraft();
        }

        Map.IsPlacingRightBoundary = placing;
        SetRightBoundaryItem.IsChecked = placing;
    }

    private void OnLeftBoundaryCompleted(TrackBoundary boundary)
    {
        _leftBoundary = boundary;
        Map.LeftBoundary = boundary;
        Map.IsPlacingLeftBoundary = false;
        SetLeftBoundaryItem.IsChecked = false;

        RebuildTrackArea();
        SaveBoundaries();
    }

    private void OnRightBoundaryCompleted(TrackBoundary boundary)
    {
        _rightBoundary = boundary;
        Map.RightBoundary = boundary;
        Map.IsPlacingRightBoundary = false;
        SetRightBoundaryItem.IsChecked = false;

        RebuildTrackArea();
        SaveBoundaries();
    }

    private void OnLeftBoundaryEdited(TrackBoundary boundary)
    {
        _leftBoundary = boundary;
        Map.LeftBoundary = boundary;

        RebuildTrackArea();
        SaveBoundaries();
    }

    private void OnRightBoundaryEdited(TrackBoundary boundary)
    {
        _rightBoundary = boundary;
        Map.RightBoundary = boundary;

        RebuildTrackArea();
        SaveBoundaries();
    }

    private void OnClearBoundariesClick(object sender, RoutedEventArgs e)
    {
        _leftBoundary = null;
        _rightBoundary = null;
        _trackArea = null;
        _zoneMask = null;

        Map.LeftBoundary = null;
        Map.RightBoundary = null;
        Map.ZoneMask = null;
        Map.IsPlacingLeftBoundary = false;
        Map.IsPlacingRightBoundary = false;
        Map.CancelBoundaryDraft();

        SetLeftBoundaryItem.IsChecked = false;
        SetRightBoundaryItem.IsChecked = false;

        RebuildTrackArea();
        SaveBoundaries();
    }

    private void OnZoneFilterClick(object sender, RoutedEventArgs e)
    {
        _zoneFilterEnabled = ZoneFilterItem.IsChecked == true;
        RebuildTrackArea();
    }

    /// <summary>
    /// Пересобирает коридор и маску точек, затем пересчитывает круги и сектора.
    /// Правую границу приводит к направлению левой.
    /// </summary>
    private void RebuildTrackArea()
    {
        _trackArea = TrackZone.Create(_leftBoundary, _rightBoundary);

        if (_trackArea is not null)
        {
            _rightBoundary = _trackArea.Right;
            Map.RightBoundary = _trackArea.Right;
        }

        Map.LeftBoundary = _leftBoundary;

        bool active = _trackArea is not null && _zoneFilterEnabled;
        _zoneMask = active && _log is not null
            ? TrackZone.BuildMask(_log.Points, _trackArea)
            : null;

        Map.ZoneMask = _zoneMask;
        RebuildTrackMenu();
        RecomputeLaps();
    }

    /// <summary>Меню «Трасса»: состояние режимов, доступность фильтра и размер зоны.</summary>
    private void RebuildTrackMenu()
    {
        SetLeftBoundaryItem.IsChecked = Map.IsPlacingLeftBoundary;
        SetRightBoundaryItem.IsChecked = Map.IsPlacingRightBoundary;
        ClearBoundariesItem.IsEnabled = _leftBoundary is not null || _rightBoundary is not null;
        ZoneFilterItem.IsEnabled = _trackArea is not null;
        ZoneFilterItem.IsChecked = _zoneFilterEnabled && _trackArea is not null;

        if (_trackArea is null)
        {
            ZoneInfoItem.Header = _leftBoundary is null && _rightBoundary is null
                ? "Границы не заданы"
                : "Нужны обе границы трассы";
            return;
        }

        int inside = TrackZone.CountInside(_zoneMask);
        int total = _log?.Points.Count ?? 0;

        ZoneInfoItem.Header = string.Create(CultureInfo.CurrentCulture,
            $"В зоне: {inside} из {total} · Л: {_trackArea.Left.Vertices.Count} · " +
            $"П: {_trackArea.Right.Vertices.Count}");
    }

    private void SaveBoundaries()
    {
        try
        {
            TrackZone.SaveBoundary(LeftBoundaryPath, _leftBoundary);
            TrackZone.SaveBoundary(RightBoundaryPath, _rightBoundary);
        }
        catch
        {
            // сохранение границ не критично для работы приложения
        }
    }

    private void TryLoadBoundaries()
    {
        _leftBoundary = TrackZone.TryLoadBoundary(LeftBoundaryPath);
        _rightBoundary = TrackZone.TryLoadBoundary(RightBoundaryPath);

        Map.LeftBoundary = _leftBoundary;
        Map.RightBoundary = _rightBoundary;
    }

    // ---------- панель показателей ----------

    private void BuildIndicatorRows(TrackLog log)
    {
        IndicatorList.Children.Clear();
        _rows.Clear();

        var saved = LoadIndicatorSelection();
        bool useSaved = saved.Count > 0;

        foreach (var column in log.Columns)
        {
            if (IsMapColumn(column.Name))
                continue; // координаты и время и так видны в статусе и слайдере

            bool isChecked = useSaved
                ? saved.Contains(column.Name)
                : DefaultIndicators.Any(d => string.Equals(d, column.Name, StringComparison.OrdinalIgnoreCase));

            var box = new CheckBox
            {
                Content = column.Name,
                IsChecked = isChecked,
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.Checked += OnIndicatorChanged;
            box.Unchecked += OnIndicatorChanged;

            var value = new TextBlock
            {
                Margin = new Thickness(8, 0, 0, 0),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var stats = new TextBlock
            {
                FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(18, 1, 0, 0),
            };

            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(value, 1);
            line.Children.Add(box);
            line.Children.Add(value);

            var rowPanel = new StackPanel { Margin = new Thickness(0, 1, 0, 6) };
            rowPanel.Children.Add(line);
            if (column.IsNumeric)
                rowPanel.Children.Add(stats);

            IndicatorList.Children.Add(rowPanel);
            _rows.Add(new IndicatorRow { Column = column, Box = box, Value = value, Stats = stats });
        }
    }

    private void OnIndicatorChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingRows)
            return;

        var box = (CheckBox)sender;
        var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Box, box));
        if (row is null)
            return;

        UpdateIndicatorRow(row, CurrentIndex);
        SaveIndicatorSelection();
    }

    private void UpdateIndicatorRow(IndicatorRow row, int index)
    {
        if (row.Box.IsChecked != true)
        {
            row.Value.Text = string.Empty;
            row.Stats.Text = string.Empty;
            return;
        }

        row.Value.Text = row.Column.FormatValue(index, CultureInfo.CurrentCulture);
        row.Stats.Text = row.Column.FormatStats(CultureInfo.CurrentCulture);
    }

    private void OnSelectAllIndicators(object sender, RoutedEventArgs e) => SetAllIndicators(true);

    private void OnClearIndicators(object sender, RoutedEventArgs e) => SetAllIndicators(false);

    private void SetAllIndicators(bool value)
    {
        _updatingRows = true;
        foreach (var row in _rows)
            row.Box.IsChecked = value;
        _updatingRows = false;

        int index = CurrentIndex;
        foreach (var row in _rows)
            UpdateIndicatorRow(row, index);

        SaveIndicatorSelection();
    }

    private int CurrentIndex => Math.Clamp((int)Math.Round(TimeSlider.Value), 0, Math.Max(0, (_log?.Points.Count ?? 1) - 1));

    private static bool IsMapColumn(string name) =>
        name.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("latitude", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("longitude", StringComparison.OrdinalIgnoreCase);

    private static string IndicatorsSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DashAnalizer",
        "indicators.txt");

    private void SaveIndicatorSelection()
    {
        try
        {
            var path = IndicatorsSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Column.Name));
        }
        catch
        {
            // сохранение выбора не критично для работы приложения
        }
    }

    private HashSet<string> LoadIndicatorSelection()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = IndicatorsSettingsPath;
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.Trim().Length > 0)
                        result.Add(line.Trim());
                }
            }
        }
        catch
        {
            // нет настроек — будут использованы показатели по умолчанию
        }

        return result;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            (Map.IsPlacingLapLine || Map.IsPlacingSectorLine ||
             Map.IsPlacingLeftBoundary || Map.IsPlacingRightBoundary))
        {
            if (Map.IsPlacingLapLine)
            {
                Map.IsPlacingLapLine = false;
                SetLapLineItem.IsChecked = false;
            }

            if (Map.IsPlacingSectorLine)
            {
                Map.IsPlacingSectorLine = false;
                RebuildSectorsMenu();
            }

            if (Map.IsPlacingLeftBoundary || Map.IsPlacingRightBoundary)
            {
                Map.IsPlacingLeftBoundary = false;
                Map.IsPlacingRightBoundary = false;
                SetLeftBoundaryItem.IsChecked = false;
                SetRightBoundaryItem.IsChecked = false;
                Map.CancelBoundaryDraft();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && (Map.IsPlacingLeftBoundary || Map.IsPlacingRightBoundary))
        {
            Map.CompleteBoundaryDraft();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && Map.DeleteSelectedVertex())
        {
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Space || _log is null)
            return;

        TogglePlay();
        e.Handled = true;
    }
}