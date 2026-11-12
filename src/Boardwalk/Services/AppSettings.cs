using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Boardwalk.Services;

public enum ThemeMode { System, Light, Dark }

/// <summary>All user preferences. Saved as JSON in %APPDATA%\Boardwalk\settings.json.</summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string Accent { get; set; } = "Ocean";
    /// <summary>Ask before deleting a card, column or board.</summary>
    public bool ConfirmDeletes { get; set; } = true;
    /// <summary>Smaller cards: title only, no notes preview.</summary>
    public bool CompactCards { get; set; } = false;
    /// <summary>Show the first lines of a card's notes on the card.</summary>
    public bool ShowNotesPreview { get; set; } = true;
    public double ColumnWidth { get; set; } = 290;
    public string LastBoardId { get; set; } = "";

    public bool RememberWindow { get; set; } = true;
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = 1280;
    public double WinHeight { get; set; } = 800;
    public bool WinMaximized { get; set; } = false;
}

public static class SettingsService
{
    /// <summary>%APPDATA%\Boardwalk. The self-test points this at a temporary folder.</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Boardwalk");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static event Action Changed;

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (s != null) Current = s;
            }
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            Current = new AppSettings();
        }
        Sanitize(Current);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, FilePath, true);
        }
        catch { /* settings are best-effort */ }
    }

    /// <summary>Save and tell the open windows to re-apply settings.</summary>
    public static void Commit()
    {
        Sanitize(Current);
        Save();
        Changed?.Invoke();
    }

    private static void Sanitize(AppSettings s)
    {
        s.ColumnWidth = double.IsNaN(s.ColumnWidth) ? 290 : Math.Clamp(s.ColumnWidth, 220, 420);
        s.LastBoardId ??= "";
        if (string.IsNullOrWhiteSpace(s.Accent)) s.Accent = "Ocean";
        if (s.WinWidth < 700 || double.IsNaN(s.WinWidth)) s.WinWidth = 1280;
        if (s.WinHeight < 450 || double.IsNaN(s.WinHeight)) s.WinHeight = 800;
    }
}
