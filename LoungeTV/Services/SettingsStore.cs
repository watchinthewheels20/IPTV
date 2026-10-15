using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LoungeTV.Services;

public sealed class SavedSettings
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
}

public static class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoungeTV");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static (string Server, string User, string Password) Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return ("", "", "");
            var settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(FilePath));
            if (settings == null) return ("", "", "");
            var password = string.IsNullOrEmpty(settings.EncryptedPassword) ? "" :
                Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(settings.EncryptedPassword), null, DataProtectionScope.CurrentUser));
            return (settings.ServerUrl, settings.Username, password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or CryptographicException or FormatException)
        {
            // Corrupt, inaccessible or copied from another Windows account: request a fresh login.
            return ("", "", "");
        }
    }

    public static void Save(string serverUrl, string username, string password)
    {
        Directory.CreateDirectory(DirectoryPath);
        var settings = new SavedSettings
        {
            ServerUrl = serverUrl,
            Username = username,
            EncryptedPassword = Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser))
        };
        var tempFile = FilePath + ".tmp";
        try
        {
            File.WriteAllText(tempFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempFile, FilePath, true);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
