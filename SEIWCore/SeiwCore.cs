using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace SEIWCore;

/// <summary>Один пользователь SEIW: имя, парольный хэш, кодовое слово восстановления, «живые» знаки.</summary>
public class SeiwUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Пользователь";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public List<string> LiveChars { get; set; } = new();
    public bool ForceChange { get; set; } = false;
    public string RecoveryCode { get; set; } = "";

    // Круговой ввод пароля (КВП): режим кода/последовательности секторов на циферблате.
    public bool UseCircular { get; set; } = false;           // включён ли круговой ввод
    public int CircularSectors { get; set; } = 12;           // число секторов на циферблате
    public string CircularHash { get; set; } = "";           // хэш последовательности индексов

    public DateTime Created { get; set; } = DateTime.Now;

    public static List<string> ComputeLiveChars(string password)
        => password.Distinct().Select(c => c.ToString()).OrderBy(c => c, StringComparer.Ordinal).ToList();

    public void SetPassword(string password)
    {
        Salt = RandomSalt();
        Hash = HashPassword(password, Salt);
        LiveChars = ComputeLiveChars(password);
    }

    public void SetCircularPassword(int sectors, IEnumerable<int> sequence)
    {
        CircularSectors = sectors;
        UseCircular = true;
        var indices = string.Join(",", sequence.Select(x => x.ToString()));
        CircularHash = HashPassword(indices, Salt);
    }

    public bool CheckCircularPassword(int sectors, IEnumerable<int> sequence)
    {
        if (!UseCircular || string.IsNullOrEmpty(CircularHash)) return false;
        if (sectors != CircularSectors) return false;
        var indices = string.Join(",", sequence.Select(x => x.ToString()));
        return HashPassword(indices, Salt) == CircularHash;
    }

    public bool CheckPassword(string password) => HashPassword(password, Salt) == Hash;

    public static string HashPassword(string password, string salt)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(salt + password);
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    private static string RandomSalt()
    {
        var b = new byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexString(b);
    }
}

/// <summary>Интервал расписания: действие в день недели в диапазоне времени.</summary>
public class ScheduleInterval
{
    public int DayOfWeek { get; set; } = -1;
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "21:00";
    public string Action { get; set; } = "Message";
    public string Message { get; set; } = "";

    public bool TryGetRange(out TimeSpan start, out TimeSpan end)
    {
        start = default; end = default;
        if (!TimeSpan.TryParse(Start, out start) || !TimeSpan.TryParse(End, out end)) return false;
        return true;
    }

    public bool IsActive(DateTime now)
    {
        if (DayOfWeek >= 0 && DayOfWeek != (int)now.DayOfWeek) return false;
        if (!TryGetRange(out var s, out var e)) return false;
        var t = now.TimeOfDay;
        if (s == e) return false;
        return s <= e ? (t >= s && t < e) : (t >= s || t < e);
    }
}

/// <summary>Хранилище пользователей и расписания (JSON рядом с программой).</summary>
public static class SeiwStore
{
    public static string UsersPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "seiw_users.json");
    public static string SchedulePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "seiw_schedule.json");
    public static string FireExitPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "seiw_fireexit.json");

    public static List<SeiwUser> LoadUsers()
    {
        try
        {
            if (!File.Exists(UsersPath)) return new List<SeiwUser>();
            return JsonSerializer.Deserialize<List<SeiwUser>>(File.ReadAllText(UsersPath, Encoding.UTF8)) ?? new();
        }
        catch { return new(); }
    }

    public static void SaveUsers(List<SeiwUser> users)
        => File.WriteAllText(UsersPath, JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

    public static List<ScheduleInterval> LoadSchedule()
    {
        try
        {
            if (!File.Exists(SchedulePath)) return new();
            return JsonSerializer.Deserialize<List<ScheduleInterval>>(File.ReadAllText(SchedulePath, Encoding.UTF8)) ?? new();
        }
        catch { return new(); }
    }

    public static void SaveSchedule(List<ScheduleInterval> intervals)
        => File.WriteAllText(SchedulePath, JsonSerializer.Serialize(intervals, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

    /// <summary>Возвращает хэш аварийного кода ПВ (hex SHA256) или null, если не задан.</summary>
    public static string? GetFireExitHash()
    {
        try
        {
            if (!File.Exists(FireExitPath)) return null;
            var json = File.ReadAllText(FireExitPath, Encoding.UTF8);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json);
            var h = doc?["Hash"]?.GetValue<string>();
            return string.IsNullOrEmpty(h) ? null : h;
        }
        catch { return null; }
    }

    public static void SaveFireExitHash(string hash)
        => File.WriteAllText(FireExitPath,
            JsonSerializer.Serialize(new { Hash = hash }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
}