using System.Reflection;

namespace TeamsAudioDucking.Core;

public static class AppInfo
{
    /// <summary>Application version, e.g. "1.1.0".</summary>
    public static string Version { get; } = GetVersion();

    private static string GetVersion()
    {
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
            return info.Split('+')[0]; // strip source-link build metadata
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
    }
}
