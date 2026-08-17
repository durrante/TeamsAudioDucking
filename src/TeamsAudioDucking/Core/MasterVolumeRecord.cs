namespace TeamsAudioDucking.Core;

/// <summary>
/// The system (endpoint) volume of one output device, captured before it was
/// raised for a call so the exact previous level can be put back afterwards.
/// Persisted so a crash mid-call still restores it on the next start.
/// </summary>
public sealed class MasterVolumeRecord
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";

    /// <summary>Level before the call, 0..1. This is what gets restored.</summary>
    public float PreviousLevel { get; set; }

    /// <summary>
    /// Level read back immediately after raising it. If the device no longer
    /// sits at this level when the call ends, the user moved the slider
    /// themselves mid-call and their choice is left alone.
    /// </summary>
    public float AppliedLevel { get; set; }

    public DateTime RaisedAtUtc { get; set; }
}
