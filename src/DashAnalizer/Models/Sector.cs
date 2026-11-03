namespace DashAnalizer.Models;

/// <summary>
/// Сектор трека: интервал между соседними границами.
/// Номер сектора начинается с 1 на каждом круге.
/// </summary>
public sealed record Sector(
    int LapNumber,
    int Number,
    int StartIndex,
    int EndIndex,
    double DurationSeconds,
    double DistanceMeters,
    double SpeedKph);

/// <summary>Палитра цветов секторов: цвет зависит от номера сектора внутри круга.</summary>
public static class SectorPalette
{
    private static readonly uint[] Rgb =
    {
        0x1E88E5, 0xE53935, 0xFB8C00, 0xF9A825,
        0x43A047, 0x00ACC1, 0x8E24AA, 0xEC407A,
    };

    /// <summary>Цвет сектора по его номеру (нумерация с 1, цвета повторяются).</summary>
    public static uint ColorFor(int number)
    {
        int index = (Math.Max(1, number) - 1) % Rgb.Length;
        return Rgb[index];
    }
}
