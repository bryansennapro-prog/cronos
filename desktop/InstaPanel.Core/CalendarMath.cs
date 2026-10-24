namespace InstaPanel.Core;

/// <summary>Intervalos do calendário. Semanas começam no domingo, como nos calendários brasileiros.</summary>
public static class CalendarMath
{
    /// <summary>Semana (domingo a sábado) que contém o dia.</summary>
    public static (DateTime Start, DateTime End) WeekOf(DateTime day)
    {
        var start = day.Date.AddDays(-(int)day.DayOfWeek);
        return (start, start.AddDays(7));
    }

    /// <summary>Grade fixa de 6 semanas que cobre o mês inteiro (sempre 42 dias).</summary>
    public static (DateTime Start, DateTime End) MonthGridOf(DateTime day)
    {
        var first = new DateTime(day.Year, day.Month, 1);
        var start = first.AddDays(-(int)first.DayOfWeek);
        return (start, start.AddDays(42));
    }

    /// <summary>Mesmo horário do dia, em outra data (para arrastar no calendário).</summary>
    public static DateTimeOffset MoveToDay(DateTimeOffset when, DateTime day, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(when, zone);
        var target = day.Date.Add(local.TimeOfDay);
        return new DateTimeOffset(target, zone.GetUtcOffset(target));
    }
}
