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

    /// <summary>Raise Teams' own session volume during calls (never the master volume).</summary>
    public bool BoostTeamsVolume { get; set; }

    /// <summary>Target Teams session volume during calls, percent. Only ever raised to this, never lowered.</summary>
    public int TeamsCallVolumePercent { get; set; } = 80;

    /// <summary>Processes never muted (in addition to Teams itself). Names without .exe.</summary>
    public List<string> ExcludedProcesses { get; set; } = new();

    /// <summary>Processes always muted during calls, even if listed as excluded.</summary>
    public List<string> AlwaysMuteProcesses { get; set; } = new();

    /// <summary>Process names treated as Microsoft Teams (never muted, used for call detection).</summary>
    public List<string> TeamsProcessNames { get; set; } = new()
    {
        "ms-teams", "msteams", "teams",
        "microsoft.teams.slimcorevdihost", "microsoft.teams.slimcorevdihost.win-x64",
    };

    [JsonIgnore]
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamsAudioDucking");

    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load settings, using defaults", ex);
        }
        return new AppSettings();
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
