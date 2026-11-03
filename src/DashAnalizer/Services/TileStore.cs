using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DashAnalizer.Services;

/// <summary>
/// Тайлы карты: память → дисковый кэш → загрузка по сети.
/// Источник задаётся шаблоном URL и папкой кэша.
/// Если сети нет (или загрузка выключена), возвращается null и карта показывает координатную сетку.
/// </summary>
public sealed class TileStore : IDisposable
{
    private const string UserAgent = "DashAnalizer/1.0 (race-track-map; WPF desktop)";

    private readonly HttpClient _http;
    private readonly Func<int, int, int, string> _tileUrl;
    private readonly string _cacheDir;
    private readonly string _fileExtension;
    private readonly ConcurrentDictionary<(int Z, int X, int Y), ImageSource?> _images = new();
    private readonly ConcurrentDictionary<(int Z, int X, int Y), byte> _requested = new();
    private volatile bool _downloadEnabled = true;
    private int _generation;

    public TileStore(Func<int, int, int, string> tileUrl, string cacheDir, string fileExtension)
    {
        _tileUrl = tileUrl;
        _cacheDir = cacheDir;
        _fileExtension = fileExtension;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>Вызывается (возможно из фонового потока) после загрузки нового тайла.</summary>
    public event Action? TileLoaded;

    public bool DownloadEnabled
    {
        get => _downloadEnabled;
        set => _downloadEnabled = value;
    }

    /// <summary>Возвращает уже загруженный тайл или null, если его ещё нет.</summary>
    public ImageSource? Get(int z, int x, int y) =>
        _images.TryGetValue((z, x, y), out var image) ? image : null;

    /// <summary>Запрашивает тайл; повторные запросы одного тайла игнорируются.</summary>
    public void Request(int z, int x, int y)
    {
        if (!_requested.TryAdd((z, x, y), 0))
            return;

        int generation = Volatile.Read(ref _generation);
        _ = LoadAsync(z, x, y, generation);
    }

    /// <summary>Сбрасывает память после импорта кэша, чтобы тайлы прочитались с диска заново.</summary>
    public void ClearMemory()
    {
        Interlocked.Increment(ref _generation);
        _images.Clear();
        _requested.Clear();
    }

    private async Task LoadAsync(int z, int x, int y, int generation)
    {
        ImageSource? image = null;

        try
        {
            image = await Task.Run(() => Load(z, x, y)).ConfigureAwait(false);
        }
        catch
        {
            image = null; // сеть/диск недоступны — вместо тайла будет сетка
        }

        // кэш очищали, пока шла загрузка — результат уже неактуален
        if (generation != Volatile.Read(ref _generation))
            return;

        _images[(z, x, y)] = image;
        TileLoaded?.Invoke();
    }

    private ImageSource? Load(int z, int x, int y)
    {
        var file = Path.Combine(
            _cacheDir,
            z.ToString(CultureInfo.InvariantCulture),
            x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture) + _fileExtension);

        if (File.Exists(file))
        {
            var cached = TryDecode(File.ReadAllBytes(file));
            if (cached is not null)
                return cached;
        }

        if (!_downloadEnabled)
            return null;

        var bytes = _http.GetByteArrayAsync(_tileUrl(z, x, y))
            .GetAwaiter()
            .GetResult();

        var image = TryDecode(bytes);
        if (image is null)
            return null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }
        catch
        {
            // кэшировать не удалось — тайл всё равно отображается
        }

        return image;
    }

    private static ImageSource? TryDecode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze(); // замороженный тайл безопасно рисовать из любого потока
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
