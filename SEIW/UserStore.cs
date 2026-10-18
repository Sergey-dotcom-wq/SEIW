using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SEIW;

/// <summary>Один учётная запись SEIW: имя, аватар, парольный хэш и набор «живых» знаков.</summary>
public class SeiwUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Пользователь";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";          // SHA256(salt+password)
    public List<string> LiveChars { get; set; } = new(); // знаки, участвующие в пароле («живые»)
    public bool ForceChange { get; set; } = false;  // принудительная смена при входе
    public string RecoveryCode { get; set; } = "";  // секретный код восстановления пароля
    public DateTime Created { get; set; } = DateTime.Now;

    /// <summary>«Живые» знаки пароля = набор уникальных знаков.<br/>
    /// Знаки, не входящие в этот набор, «спят»: отображаются в поле, но к доступу не ведут.</summary>
    public static List<string> ComputeLiveChars(string password)
    {
        return password.Distinct()
            .Select(c => c.ToString())
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    public void SetPassword(string password)
    {
        Salt = RandomSalt();
        Hash = HashPassword(password, Salt);
        LiveChars = ComputeLiveChars(password);
    }

    public bool CheckPassword(string password)
    {
        return HashPassword(password, Salt) == Hash;
    }

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

/// <summary>Хранение списка пользователей в JSON-файле рядом с программой.</summary>
public static class UserStore
{
    private static readonly string Dir = AppContext.BaseDirectory;
    private static readonly string FilePath = Path.Combine(Dir, "seiw_users.json");

    public static List<SeiwUser> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<SeiwUser>();
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<SeiwUser>>(json) ?? new List<SeiwUser>();
        }
        catch { return new List<SeiwUser>(); }
    }

    public static void Save(List<SeiwUser> users)
    {
        var json = JsonSerializer.Serialize(users, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json, Encoding.UTF8);
    }
}