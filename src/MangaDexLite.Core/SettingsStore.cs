using System.Text.Json;

namespace MangaDexLite.Core;

public sealed record AppSettings(string Directory = "", bool Cbz = false, bool DataSaver = false, string Language = "ru");
public sealed record SettingsLoad(AppSettings Settings, string? Warning);

public sealed class SettingsStore(string path)
{
    private bool preserveFile;
    public async Task<SettingsLoad> LoadAsync()
    {
        if (!File.Exists(path)) return new(new(), null);
        try
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var root = json.RootElement;
            var issues = new List<string>();
            string ReadString(string key, string fallback)
            {
                if (!root.TryGetProperty(key, out var value)) return fallback;
                if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
                issues.Add(key);
                return fallback;
            }
            bool ReadBool(string key)
            {
                if (!root.TryGetProperty(key, out var value)) return false;
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
                issues.Add(key);
                return false;
            }
            string directory = ReadString("Directory", "");
            if (directory.Length > 0 && !Path.IsPathFullyQualified(directory)) { issues.Add("Directory"); directory = ""; }
            string language = ReadString("Language", "ru");
            if (language.Length > 10 || language.Any(c => !char.IsAsciiLetter(c) && c != '-')) { issues.Add("Language"); language = "ru"; }
            var settings = new AppSettings(directory, ReadBool("Cbz"), ReadBool("DataSaver"), language);
            foreach (var field in root.EnumerateObject())
                if (field.Name is not ("Directory" or "Cbz" or "DataSaver" or "Language")) issues.Add(field.Name);
            // Preserve files containing unknown/invalid fields for an explicit user repair.
            preserveFile = issues.Count > 0;
            return new(settings, issues.Count == 0 ? null : "Некорректные или неизвестные настройки пропущены: " + string.Join(", ", issues) + ". Файл настроек сохранён без изменений.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            preserveFile = true;
            return new(new(), "Не удалось прочитать настройки. Использованы стандартные значения; исходный файл сохранён. " + ex.Message);
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        if (preserveFile) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
