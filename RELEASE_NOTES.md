# PrimeDictate 4.1.0

## Highlights

- Added launch-at-login support. MSI installs enable it by default, silent installs can opt out with `LAUNCHATLOGIN=0`, Chocolatey supports `/NoLaunchAtLogin`, and the app exposes `--enable-launch-at-login` / `--disable-launch-at-login` switches.
- Added a local-first Impact tab in Settings with words typed, estimated net time saved, average speaking WPM, 14-day word bars, and milestone achievements.
- Added local achievement notifications for dictation word-count milestones.
- Reduced live-preview CPU work by using the recorder's RMS signal for silence timing instead of repeatedly resampling snapshots just to detect speech.
- Kept the refactor scoped: final-only text injection, overlay-only live preview, model discovery, and Whisper disposal behavior are unchanged.
