# Teams Audio Ducking

A lightweight Windows 11 system-tray utility that automatically mutes every
non-Teams application's audio while you are in a Microsoft Teams call or
meeting, and restores each application to its exact previous state (mute flag
and volume level) when the call ends.

- No internet access, no telemetry, no Teams credentials, no audio recording.
- No admin rights needed: per-user install, per-user startup, per-session audio control.
- Event-driven; near-zero CPU when idle.

## How Teams call detection works (and its limits)

There is **no public local API** that exposes the new Teams client's call
state. The most reliable Windows-level signal is:

> Teams holds an open **microphone capture stream** for the entire duration of
> a call or meeting, even while you are muted inside Teams (it keeps the
> stream open so unmuting is instant).

Windows records per-app microphone usage under
`HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone`:
while an app is actively using the mic, its `LastUsedTimeStop` value is `0`.

This utility watches that key with `RegNotifyChangeKeyValue` (event-driven, no
polling) and treats **"Teams is using the microphone AND a Teams process is
running"** as *in a call*. Details:

- Covers the new Teams client (`ms-teams.exe`, MSIX package `MSTeams_...`) and
  classic Teams (`Teams.exe`, non-packaged), on any microphone device.
- The process-liveness check ends the call state promptly if Teams crashes.
- A 2-second debounce on "call ended" ignores the brief mic release that
  happens when Teams switches audio devices mid-call.
- A 5-second reconciliation sweep is a safety net for missed events (for
  example around sleep/resume). It is a couple of registry reads when idle.

### Ring detection (v1.1)

The microphone signal only appears once a call is *connected*, so with the
mic signal alone muting would start after you (or the other side) picks up,
not while the call is ringing. To catch the ringing phase, the utility also
watches Teams' own **playback** sessions via audio-session state events:

- Teams playing audio continuously for **1.5 seconds or more** (an outbound
  ringback tone, or an incoming ring) starts the muting early. Short
  notification chimes never last that long.
- Once the call connects (mic in use), the mic signal alone decides when the
  call ends, so the hang-up tone cannot delay the restore. A ring that is
  never answered un-mutes as soon as the ringing stops (plus the usual
  2-second debounce).
- Sessions the utility merely *finds* (rather than sees being created) must go
  quiet once before their activity counts, so a Teams session that idles in an
  "active" state cannot fake a permanent call.
- Side effect: any sustained Teams media playback (e.g. a long voice message)
  also triggers muting until it stops. If that bothers you, untick *"Start
  muting while a Teams call is ringing"* in Settings; call-connected detection
  is unaffected.

If ring detection does not fire on your machine, set `"TraceSessionEvents":
true` in `settings.json` (see below), reproduce an outbound call, and check
the log: it will record which process plays the ringback so the process list
can be adjusted.

### Known limitations

| Scenario | Behaviour |
| --- | --- |
| Mic test in Teams settings, recording a video clip in chat | Detected as a "call" (mic in use). Other apps get muted until you finish. |
| Joining a meeting with **"Don't use audio"** / view-only webinars & live events | Teams never opens the mic, so no call is detected and nothing is muted. |
| Teams in a **browser** (teams.microsoft.com) | Not detected (the mic use belongs to the browser). The browser would also be muted during detected calls; add it to the exclusion list if you use web Teams alongside the desktop app. |
| Windows privacy settings block Teams' microphone access | The signal never fires; detection cannot work. |

### Teams process/audio-session structure

The new Teams client plays all call audio from `ms-teams.exe` and may create
several simultaneous audio sessions (call audio, ringtones, notifications) on
any render device. Some installs also carry the Teams media-engine host
package (`Microsoft.Teams.SlimCoreVdiHost`), which can own the microphone
stream instead of `ms-teams.exe`. All sessions belonging to `ms-teams` /
`msteams` / `teams` / the SlimCore host are exempt from muting, on every
device, including sessions created mid-call, so Teams can never be muted by
this tool regardless of how it arranges its sessions.

## What the muting does (and never does)

Uses the Windows Core Audio session APIs (WASAPI, via the NAudio.Wasapi
wrapper) to control **individual application audio sessions**:

- Enumerates all active render devices and their sessions; mutes every session
  that is not Teams, not this utility, and not excluded (both *active* and
  *inactive* sessions, so a paused Spotify cannot blast mid-call).
- Records the exact prior state (mute flag + volume) per session before
  touching it, and restores exactly that afterwards. Sessions that were
  already muted are left completely untouched.
- New audio sessions appearing during a call are muted immediately via
  `IAudioSessionNotification` (session-created events).
- Apps that restart during a call are re-muted, with their **original**
  pre-call state carried over (Windows persists per-app mute, so this matters).
- Apps closed while muted are restored the moment their session reappears
  (state is kept for up to 24 hours).
- Mute state is persisted to disk, so if the utility itself is killed
  mid-call, the next start restores everything.
- Optionally (off by default) raises **Teams' own** session volume to a
  configurable level during calls, so the call is louder without touching the
  master volume. It only ever *raises* Teams (never lowers it), applies once
  per call (dragging the slider down mid-call is respected), and the prior
  volume is restored exactly when the call ends.

Never touched: master volume, microphone, default playback device, device
enable/disable state, Teams' own sessions.

## Building

Requirements: Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
First build needs internet access for NuGet restore (single dependency: NAudio.Wasapi).

```powershell
# Self-contained single file (no runtime needed on the target machine):
.\build.ps1

# Or a small framework-dependent build (target needs the .NET 8 Desktop Runtime):
.\build.ps1 -FrameworkDependent
```

Output lands in `.\publish\TeamsAudioDucking.exe`.

## Installing

Two options, both per-user and admin-free:

### Option A: PowerShell script

```powershell
.\installer\install.ps1     # builds if needed, installs, adds Start-menu + startup, launches
.\installer\uninstall.ps1   # removes everything (keeps settings/logs)
```

### Option B: Inno Setup installer

```powershell
.\build.ps1
iscc installer\TeamsAudioDucking.iss   # needs Inno Setup 6
```

Produces `installer\Output\TeamsAudioDucking-Setup-1.2.0.exe` with a proper
uninstaller, Start-menu shortcut and an optional "start at sign-in" task.

Installs to `%LOCALAPPDATA%\Programs\TeamsAudioDucking`. Startup uses the
per-user `HKCU\...\CurrentVersion\Run` key.

## Using it

It sits in the tray:

- **Green** icon: enabled, idle.
- **Red with a slash**: in a Teams call, other apps muted.
- **Grey**: disabled.

Right-click for status ("In Teams call", "Muted N applications"),
enable/disable, manual mute/restore, Settings and Exit. Double-click opens
Settings.

Right-clicking also offers "About Teams Audio Ducking", which shows the
version and data locations (the version is in the tray tooltip and at the
bottom of the Settings window too).

Settings (also editable as JSON, see below):

- Enable/disable automatic muting
- Start with Windows
- Mute Windows system sounds too (off by default)
- Start muting while a Teams call is ringing (on by default; see
  *Ring detection* above)
- Raise Teams' own volume during calls to a set percentage (off by default)
- Exclusion list: processes never muted (one per line, e.g. `spotify`)
- Always-mute list: processes muted during calls even if excluded

By default only Microsoft Teams is exempt.

JSON-only diagnostic option: `"TraceSessionEvents": true` logs every audio
session creation, Teams playback transitions and a periodic session snapshot.
Leave it off normally; it makes the log chatty.

Note: "Restore audio now" during an active call sticks until the next call
starts or ends; the utility will not re-mute behind your back.

## Files

| Path | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\TeamsAudioDucking\settings.json` | settings |
| `%LOCALAPPDATA%\TeamsAudioDucking\muted-state.json` | crash-recovery mute state |
| `%LOCALAPPDATA%\TeamsAudioDucking\logs\TeamsAudioDucking.log` | timestamped event log (calls detected/ended, apps muted/restored, errors; 2 MB rotation) |

## Source layout

```text
src/TeamsAudioDucking/
  App.xaml(.cs)              application wiring, timers, power/session events
  Assets/app.ico             application icon (generated by tools/make-icon.ps1)
  Core/TeamsCallDetector.cs  registry + playback-based Teams call detection
  Core/AudioDucker.cs        WASAPI session mute/restore engine + Teams playback watch
  Core/AppSettings.cs        JSON settings
  Core/AppInfo.cs            assembly version helper
  Core/StartupManager.cs     HKCU Run key management
  Core/MuteRecord.cs         persisted per-session state
  Core/Logger.cs             rotating file logger
  Tray/TrayIcon.cs           tray icon + menu (icons drawn at runtime)
  UI/SettingsWindow.xaml(.cs) settings window (WPF)
installer/                   Inno Setup script + PowerShell install/uninstall
tools/make-icon.ps1          regenerates Assets/app.ico
build.ps1                    publish helper
```

## Changelog

- **1.2.0**: optional Teams call-volume boost: raise Teams' own session volume
  to a configurable percentage during calls, restored exactly afterwards
  (never the master volume, never lowered).
- **1.1.0**: muting now starts while a call is ringing (Teams playback
  heuristic, can be disabled in Settings); About menu item with version in
  the tray; proper application icon (window title bars, taskbar, Explorer);
  `TraceSessionEvents` diagnostic setting.
- **1.0.0**: initial release.
