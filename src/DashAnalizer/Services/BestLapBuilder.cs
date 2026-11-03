using DashAnalizer.Models;

namespace DashAnalizer.Services;

/// <summary>
/// Сборка лучшего круга из секторов разных кругов.
/// По умолчанию для каждого номера берётся самый быстрый сектор.
/// Переопределения задают номер круга для конкретного номера сектора.
/// </summary>
public static class BestLapBuilder
{
    /// <summary>
    /// Строит лучший круг. Пустой список секторов даёт null.
    /// Номера, которых нет ни в одном круге, в состав не попадают.
    /// </summary>
    public static BestLap? Build(
        IReadOnlyList<Sector> sectors,
        IReadOnlyDictionary<int, int> overrides)
    {
        if (sectors.Count == 0)
            return null;

        var items = new List<BestLapItem>();
        double seconds = 0;
        double meters = 0;

        foreach (var group in sectors.GroupBy(sector => sector.Number).OrderBy(group => group.Key))
        {
            // самый быстрый вариант; при равенстве — круг с меньшим номером
            var chosen = group
                .OrderBy(sector => sector.DurationSeconds)
                .ThenBy(sector => sector.LapNumber)
                .First();
            bool manual = false;

            if (overrides.TryGetValue(group.Key, out int lapNumber))
            {
                var replacement = group.FirstOrDefault(sector => sector.LapNumber == lapNumber);
                if (replacement is not null)
                {
                    chosen = replacement;
                    manual = true;
                }
            }

            items.Add(new BestLapItem(chosen, manual));
            seconds += chosen.DurationSeconds;
            meters += chosen.DistanceMeters;
        }

        if (items.Count == 0)
            return null;

        int expected = sectors.Max(sector => sector.Number);
        return new BestLap(items, seconds, meters, expected);
    }
}
