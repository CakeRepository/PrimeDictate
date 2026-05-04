# PrimeDictate 4.4.3

## Highlights

- Fixed update handoff so major-upgrade MSIs install normally instead of using repair/reinstall flags that can leave self-contained runtime files unregistered.
- Added installer build validation for self-contained .NET runtime files in the publish output and MSI payload.
- Added a configurable start / stop dictation voice phrase. The default is `thank you`, and the phrase is removed before final text injection.

# PrimeDictate 4.4.0

## Highlights

- Added native ARM64 publish and online MSI packaging for Copilot+ PCs.
- Updated GitHub Actions release publishing to build x64 and ARM64 MSIs and attach both to tagged releases.
- Updated Chocolatey packaging to download the correct GitHub Release MSI for the machine architecture and verify it with SHA256 before install.

# PrimeDictate 4.1.0

## Highlights

- Added launch-at-login support. MSI installs enable it by default, silent installs can opt out with `LAUNCHATLOGIN=0`, Chocolatey supports `/NoLaunchAtLogin`, and the app exposes `--enable-launch-at-login` / `--disable-launch-at-login` switches.
- Added a local-first Impact tab in Settings with words typed, estimated net time saved, average speaking WPM, 14-day word bars, and milestone achievements.
- Added local achievement notifications for dictation word-count milestones.
- Reduced live-preview CPU work by using the recorder's RMS signal for silence timing instead of repeatedly resampling snapshots just to detect speech.
- Kept the refactor scoped: final-only text injection, overlay-only live preview, model discovery, and Whisper disposal behavior are unchanged.
