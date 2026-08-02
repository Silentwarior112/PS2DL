using System.Text.Json;

namespace PS2Iso.Gui;

/// <summary>Small persisted UI state (last-used folder) stored next to the executable.</summary>
public sealed class AppSettings
{
    public string LastDirectory { get; set; } = "";

    private static string Path => System.IO.Path.Combine(
        AppContext.BaseDirectory, "ps2isotool.settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new();
        }
        catch { /* ignore corrupt settings */ }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this)); }
        catch { /* non-fatal */ }
    }

    public void Remember(string? pathForFolder)
    {
        if (string.IsNullOrWhiteSpace(pathForFolder)) return;
        try
        {
            LastDirectory = Directory.Exists(pathForFolder)
                ? pathForFolder
                : System.IO.Path.GetDirectoryName(pathForFolder) ?? LastDirectory;
            Save();
        }
        catch { /* ignore */ }
    }
}
