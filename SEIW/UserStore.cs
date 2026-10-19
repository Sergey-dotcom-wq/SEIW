using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace SEIW;

/// <summary>Один учётная запись SEIW: имя, аватар, парольная схема и набор «живых» знаков.</summary>
public class SeiwUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Пользователь";
    public string Salt { get; set; } = "";                    // соль для Argon2id (hex)
#if RETAIN_PLAIN_RECOVERY
    public string RecoveryCode { get; set; } = "";             // УСТАРЕЛО: больше не храним открытый код
#endif
    public string RecoveryHash { get; set; } = "";             // Argon2id-хеш кода восстановления (HEX)
    public List<string> LiveChars { get; set; } = new();       // знаки, участвующие в пароле («живые»)
    public bool ForceChange { get; set; } = false;             // принудительная смена при входе
    public DateTime Created { get; set; } = DateTime.Now;

    /// <summary>«Живые» знаки пароля = набор уникальных знаков.
    /// Знаки, не входящие в этот набор, «спят»: отображаются в поле, но к доступу не ведут.</summary>
    public static List<string> ComputeLiveChars(string password)
    {
        return password.Distinct()
            .Select(c => c.ToString())
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    // ===== Пароль =====
    public void SetPassword(string password)
    {
        Salt = PasswordHasher.MakeSalt();
        Hash = PasswordHasher.Hash(password, Salt);
        LiveChars = ComputeLiveChars(password);
    }

    public bool CheckPassword(string password)
    {
        return PasswordHasher.Verify(password, Salt, Hash);
    }

    public string Hash { get; set; } = ""; // Argon2id-хеш пароля (HEX)

    // ===== Код восстановления =====
    /// <summary>Задать новый код восстановления: хранится только в виде Argon2id-хеша.</summary>
    public void SetRecoveryCode(string code)
    {
        RecoveryHash = PasswordHasher.HashRecovery(code);
    }

    /// <summary>Проверить код восстановления по хешу.</summary>
    public bool CheckRecoveryCode(string code)
    {
        return PasswordHasher.VerifyRecovery(code, RecoveryHash);
    }

    public bool HasRecoveryCode => !string.IsNullOrEmpty(RecoveryHash);
}

/// <summary>Хеширование паролей и кодов восстановления через Argon2id (OWASP-рекомендация).</summary>
public static class PasswordHasher
{
    // Argon2id параметры из OWASP Password Storage Cheat Sheet (min: m=19 MiB, t=2, p=1).
    private const int MemorySize = 19456; // KiB
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashSize = 32;

    public static string MakeSalt()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(SaltBytes));
    }

    /// <summary>Хеш пароля (соль передаётся отдельно, чтобы Argon2id не был «одноразовым» при смене пароля).</summary>
    public static string Hash(string password, string saltHex)
    {
        byte[] salt = Convert.FromHexString(saltHex);
        byte[] hash = Argon2Id(password, salt);
        return Convert.ToHexString(hash);
    }

    public static bool Verify(string password, string saltHex, string hashHex)
    {
        if (string.IsNullOrEmpty(hashHex) || string.IsNullOrEmpty(saltHex)) return false;
        try
        {
            byte[] salt = Convert.FromHexString(saltHex);
            byte[] hash = Argon2Id(password, salt);
            return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(hashHex));
        }
        catch { return false; }
    }

    /// <summary>Код восстановления: отдельный salt + хеш (NIST: хранить только в хешированной форме).</summary>
    public static string HashRecovery(string code)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Argon2Id(code, salt);
        return Convert.ToHexString(salt) + ":" + Convert.ToHexString(hash);
    }

    public static bool VerifyRecovery(string code, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split(':');
        if (parts.Length != 2) return false; // несовместимый (старый) формат
        try
        {
            byte[] salt = Convert.FromHexString(parts[0]);
            byte[] hash = Argon2Id(code, salt);
            var storedHash = Convert.FromHexString(parts[1]);
            return CryptographicOperations.FixedTimeEquals(hash, storedHash);
        }
        catch { return false; }
    }

    private static byte[] Argon2Id(string secret, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt,
            DegreeOfParallelism = Parallelism,
            MemorySize = MemorySize,
            Iterations = Iterations
        };
        return argon.GetBytes(HashSize);
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