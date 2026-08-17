namespace TeamsAudioDucking.Core;

/// <summary>
/// The exact pre-mute state of one audio session we changed, so it can be
/// restored precisely. Persisted to disk so a crash of this utility (or a
/// forced termination mid-call) can still be recovered on the next start.
/// </summary>
public sealed class MuteRecord
{
    public string DeviceId { get; set; } = "";
    public string SessionInstanceId { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int Pid { get; set; }
    public bool IsSystemSounds { get; set; }
    public bool WasMuted { get; set; }
    public float PreviousVolume { get; set; }
    public DateTime MutedAtUtc { get; set; }

    /// <summary>
    /// True for records where only the volume was changed (Teams call-volume
    /// boost): restore must not touch the session's mute flag.
    /// </summary>
    public bool VolumeOnly { get; set; }
}
