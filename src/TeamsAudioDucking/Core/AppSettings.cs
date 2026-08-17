using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsAudioDucking.Core;

public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool MuteSystemSounds { get; set; }

    /// <summary>Also start muting while a Teams call is ringing (sustained Teams playback heuristic).</summary>
    public bool MuteWhileRinging { get; set; } = true;

    /// <summary>Diagnostic: log audio session creations and periodic session snapshots.</summary>
    public bool TraceSessionEvents { get; set; }

    // JSON names kept from 1.2.0 so existing settings files carry over. Up to
    // 1.3.0 these raised Teams' own app volume; they now raise the system
    // volume of the device Teams plays through, which is what actually makes a
    // quiet call louder.

    /// <summary>Raise the system volume of Teams' output device during calls.</summary>
    [JsonPropertyName("BoostTeamsVolume")]
    public bool BoostCallVolume { get; set; }

    /// <summary>Target system volume during calls, percent. Only ever raised to this, never lowered.</summary>
    [JsonPropertyName("TeamsCallVolumePercent")]
    public int CallVolumePercent { get; set; } = 80;

    /// <summary>Processes never muted (in addition to Teams itself). Names without .exe.</summary>
    public List<string> ExcludedProcesses { get; set; } = new();

    /// <summary>Processes always muted during calls, even if listed as excluded.</summary>
    public List<string> AlwaysMuteProcesses { get; set; } = new();

    // MsTeamsVdi is the local media client Teams uses inside AVD/Windows 365
    // sessions with media optimisation: it plays the call audio on this
    // machine, so it must never be muted.
    private static readonly string[] BuiltInTeamsProcessNames =
    {
        "ms-teams", "msteams", "teams",
        "microsoft.teams.slimcorevdihost", "microsoft.teams.slimcorevdihost.win-x64",
        "msteamsvdi",
    };

    /// <summary>Process names treated as Microsoft Teams (never muted, used for call detection).</summary>
    public List<string> TeamsProcessNames { get; set; } = new(BuiltInTeamsProcessNames);

    [JsonIgnore]
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamsAudioDucking");

    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(SettingsPath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load settings, using defaults", ex);
        }
        // Settings saved by an older version keep working when a new Teams
        // process becomes known: merge the built-ins back in.
        foreach (var name in BuiltInTeamsProcessNames)
        {
            if (!settings.TeamsProcessNames.Any(t => Normalize(t) == Normalize(name)))
                settings.TeamsProcessNames.Add(name);
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
            Logger.Info("Settings saved");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save settings", ex);
        }
    }

    /// <summary>Lower-case, trimmed, ".exe" stripped, for comparisons.</summary>
    public static string Normalize(string processName)
    {
        var n = processName.Trim().ToLowerInvariant();
        if (n.EndsWith(".exe")) n = n[..^4];
        return n;
    }

    public bool IsTeamsProcess(string processName)
    {
        var n = Normalize(processName);
        return TeamsProcessNames.Any(t => Normalize(t) == n);
    }

    public bool IsExcluded(string processName)
    {
        var n = Normalize(processName);
        return ExcludedProcesses.Any(t => Normalize(t) == n);
    }

    public bool IsAlwaysMuted(string processName)
    {
        var n = Normalize(processName);
        return AlwaysMuteProcesses.Any(t => Normalize(t) == n);
    }
}
