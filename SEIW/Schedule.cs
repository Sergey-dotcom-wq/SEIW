using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace SEIW;

/// <summary>Один интервал расписания: действие в указанный день недели в диапазоне времени.</summary>
public class ScheduleInterval
{
    /// <summary>День недели: 0 = воскресенье … 6 = суббота (как DateTime.DayOfWeek). -1 = каждый день.</summary>
    public int DayOfWeek { get; set; } = -1;

    /// <summary>Начало интервала, формат "HH:mm".</summary>
    public string Start { get; set; } = "09:00";

    /// <summary>Конец интервала, формат "HH:mm".</summary>
    public string End { get; set; } = "21:00";

    /// <summary>Действие по истечении/в интервале: Lock, Shutdown, Message.</summary>
    public string Action { get; set; } = "Message"; // Lock | Shutdown | Message

    /// <summary>Сообщение, если Action == Message.</summary>
    public string Message { get; set; } = "";

    /// <summary>Парсит "HH:mm" в TimeSpan. Возвращает false при неверном формате.</summary>
    public bool TryGetRange(out TimeSpan start, out TimeSpan end)
    {
        start = default; end = default;
        if (!TimeSpan.TryParse(Start, out start) || !TimeSpan.TryParse(End, out end)) return false;
        return true;
    }
}

/// <summary>Хранение расписания в JSON-файле рядом с программой.</summary>
public static class ScheduleStore
{
    private static readonly string Dir = AppContext.BaseDirectory;
    private static readonly string FilePath = Path.Combine(Dir, "seiw_schedule.json");

    public static List<ScheduleInterval> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<ScheduleInterval>();
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<ScheduleInterval>>(json) ?? new List<ScheduleInterval>();
        }
        catch { return new List<ScheduleInterval>(); }
    }

    public static void Save(List<ScheduleInterval> intervals)
    {
        var json = JsonSerializer.Serialize(intervals, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json, Encoding.UTF8);
    }
}