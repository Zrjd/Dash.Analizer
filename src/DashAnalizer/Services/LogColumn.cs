using System.Globalization;

namespace DashAnalizer.Services;

/// <summary>
/// Колонка CSV-лога для панели показателей. Значения читаются в память только для отображения;
/// сам файл лога не изменяется. Числовые колонки хранятся как double (NaN = пустое значение).
/// </summary>
public sealed class LogColumn
{
    private readonly double[] _numbers;
    private readonly string?[] _texts;

    internal LogColumn(string name, int index, double[] numbers, string?[] texts, int parsedCount, int failedCount)
    {
        Name = name;
        Index = index;
        _numbers = numbers;
        _texts = texts;

        // колонка считается числовой, если разбираются все непустые значения
        IsNumeric = parsedCount > 0 && failedCount == 0;

        if (IsNumeric)
        {
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            int count = 0;

            foreach (var value in numbers)
            {
                if (double.IsNaN(value))
                    continue;

                if (value < min) min = value;
                if (value > max) max = value;
                sum += value;
                count++;
            }

            Min = count > 0 ? min : double.NaN;
            Max = count > 0 ? max : double.NaN;
            Average = count > 0 ? sum / count : double.NaN;
        }
        else
        {
            Min = double.NaN;
            Max = double.NaN;
            Average = double.NaN;
        }
    }

    public string Name { get; }

    public int Index { get; }

    /// <summary>Истина, если все непустые значения колонки — числа.</summary>
    public bool IsNumeric { get; }

    public double Min { get; }
    public double Max { get; }
    public double Average { get; }

    /// <summary>Значение в точке <paramref name="index"/> для отображения в панели.</summary>
    public string FormatValue(int index, CultureInfo culture)
    {
        if (index < 0 || index >= _numbers.Length)
            return "—";

        if (IsNumeric)
        {
            var value = _numbers[index];
            return double.IsNaN(value) ? "—" : value.ToString("0.#######", culture);
        }

        var text = _texts[index];
        return string.IsNullOrWhiteSpace(text) ? "—" : text;
    }

    /// <summary>Строка min/max/avg для числовой колонки.</summary>
    public string FormatStats(CultureInfo culture)
    {
        if (!IsNumeric || double.IsNaN(Min))
            return string.Empty;

        return string.Create(culture, $"min {Min:0.##} · max {Max:0.##} · avg {Average:0.##}");
    }

    /// <summary>Числовое значение в точке index (NaN — пусто или колонка не числовая).</summary>
    public double NumberAt(int index) =>
        index >= 0 && index < _numbers.Length && IsNumeric ? _numbers[index] : double.NaN;
}