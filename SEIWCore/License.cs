using System.Security.Cryptography;
using System.Text;

namespace SEIWCore;

/// <summary>
/// Лёгкая активация по одноразовому ключу (без интернета и привязки к железу).
/// Мастер-соль хранится в бинарнике; ключ = первые 16 hex от SHA256(masterSalt + кодовое_слово_ключа).
/// Файл активации (рядом с программой) хранит хэш использованных ключей — один ключ можно ввести только раз.
/// </summary>
public static class License
{
    private const string MasterSalt = "SEIW_LIC_2026_9f2c#";
    public const string LicenseFileName = "seiw_license.json";

    /// <summary>Вычисляет нормализованный ключ из сырого ввода.
    /// Соль лицензии НЕ используется в формуле ПВ-кода, поэтому ПВ-код никогда
    /// не является валидным лицензионным ключом (формулы полностью разделены).</summary>
    public static string ComputeKeyHash(string raw)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(MasterSalt + "|KEY|" + raw.Trim());
        return Convert.ToHexString(sha.ComputeHash(bytes)).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>Проверка одноразового ключа: верный И ещё не использованный — активирует.</summary>
    public static bool TryActivate(string rawKey, string installPath)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return false;
        var keyHash = ComputeKeyHash(rawKey);

        var used = LoadUsedKeys(installPath);
        if (used.Contains(keyHash)) return false; // ключ уже введён кем-то

        used.Add(keyHash);
        SaveUsedKeys(installPath, used);
        return true;
    }

    /// <summary>Проверяет, активирована ли установка (активация сохраняется в файле).</summary>
    public static bool IsActivated(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return false;
        var path = Path.Combine(installPath, LicenseFileName);
        if (!File.Exists(path)) return false;
        try
        {
            var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            return doc?["activated"]?.GetValue<bool>() == true;
        }
        catch { return false; }
    }

    /// <summary>Помечает установку активированной.</summary>
    public static void SetActivated(string installPath)
    {
        var path = Path.Combine(installPath, LicenseFileName);
        File.WriteAllText(path,
            System.Text.Json.JsonSerializer.Serialize(new { activated = true, at = DateTime.Now },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static HashSet<string> LoadUsedKeys(string installPath)
    {
        var used = new HashSet<string>();
        var path = Path.Combine(installPath, "seiw_used_keys.txt");
        if (!File.Exists(path)) return used;
        foreach (var line in File.ReadAllLines(path))
            if (!string.IsNullOrWhiteSpace(line)) used.Add(line.Trim().ToLowerInvariant());
        return used;
    }

    private static void SaveUsedKeys(string installPath, HashSet<string> keys)
    {
        var path = Path.Combine(installPath, "seiw_used_keys.txt");
        File.WriteAllLines(path, keys.OrderBy(k => k), Encoding.UTF8);
    }
}