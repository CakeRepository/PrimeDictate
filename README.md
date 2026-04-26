# PrimeDictate

A locally hosted, global hotkey dictation utility for fast desktop workflows. It captures the default microphone, shows live Whisper transcription in a small overlay, and types the final transcript into the focused application after a silence auto-commit or manual stop using SharpHook (no synthetic paste, no clipboard round-trip on the hot path).

## Features

- **Global hotkey**: Configurable global toggle (default `Ctrl+Shift+Space`) to start/stop recording.
- **Tray workspace UI**: Open **Workspace** from the tray icon to browse per-session dictation threads and global runtime logs in a clearer, column-based dashboard layout.
- **Log signal over noise**: Repeated adjacent log entries are collapsed (for example `(... x12)`) and history is capped to keep memory usage predictable.
- **Live preview overlay**: While recording, the app periodically re-transcribes the growing buffer and shows the current hypothesis in a non-activating overlay.
- **Silence auto-commit**: When speech has stopped for the configured delay (default 3 seconds), PrimeDictate stops capture, runs a final transcription pass, and sends the final text with `SimulateTextEntry`.
- **Transcript history**: Every committed transcript is saved to local history so you can review past dictations, recover text sent to the wrong app, and copy transcript text (with or without metadata).
- **History filters and detail view**: History includes a filter dropdown (**All**, **Injected**, **NotInjected**) plus an expanded detail pane for full transcript and target metadata.
- **History entry points**: Open history from the tray menu, from the settings window, or from the workspace toolbar.
- **Final-only target typing**: The target editor is not mutated while recording. Live corrections stay in the overlay so code editors and IntelliSense are not fighting backspace/retype updates.
- **Coding mode**: Optional setting sends an Enter key immediately after a successful transcript commit.
- **Foreground guard**: The foreground window is captured when recording starts; if focus changes before the transcript is ready, injection is skipped rather than typing into the wrong app.
- **Built-in pointer cue**: If Windows Mouse Sonar is enabled, PrimeDictate pulses it on recording/processing transitions by tapping Ctrl. It does not draw a custom pointer overlay or change the user's Windows setting.
- **Audio**: Windows default capture device via NAudio **WASAPI** (`WasapiCapture`), resampled to **16 kHz, 16-bit, mono PCM** for Whisper.
- **Mic isolation mode (best effort)**: Optional exclusive-capture setting can block other apps from the mic on supported devices; if exclusive capture fails, PrimeDictate automatically falls back to shared mode and continues dictation.
- **Inference**: [Whisper.net](https://www.nuget.org/packages/Whisper.net) `1.9.0` with `Whisper.net.Runtime` plus optional **CUDA** and **Vulkan** runtimes for hardware acceleration when available.
- **Injection**: [SharpHook](https://www.nuget.org/packages/SharpHook) `EventSimulator` for Unicode text entry (no synthetic paste, no clipboard round-trip on the hot path).

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or a compatible SDK that can build `net8.0` projects).
- **Windows** is the primary target (WASAPI capture path). Other platforms may require a different capture implementation.
- For CPU Whisper at runtime, the published **Whisper.net** requirements apply (for example, Visual C++ redistributable and instruction-set expectations); see the [Whisper.net readme](https://www.nuget.org/packages/Whisper.net).

## Model file

The app looks for a GGML model named **`ggml-large-v3-turbo.bin`**. Suggested source: the [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) collection on Hugging Face (for example, `ggml-large-v3-turbo` in the [model table](https://huggingface.co/ggerganov/whisper.cpp)).

Place the file in one of the following (first match wins):

1. Path in the `PRIME_DICTATE_MODEL` environment variable (full path to the `.bin` file).
2. `./models/ggml-large-v3-turbo.bin` (relative to the process current working directory, when that path exists).
3. `AppContext.BaseDirectory` + `models/ggml-large-v3-turbo.bin` (useful if you copy the model next to the published app).
4. A walk upward from the current directory to find `models/ggml-large-v3-turbo.bin` (helps when `dotnet run` uses a `bin/...` working directory but the repository root contains `models/`).

**Example (PowerShell, from repo root)**, downloading the file named in the upstream `main` file list:

```powershell
New-Item -ItemType Directory -Force -Path "models" | Out-Null
Invoke-WebRequest -Uri "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin" -OutFile "models/ggml-large-v3-turbo.bin"
```

The model is large (on the order of 1.5 GiB). First transcription after launch loads it and may take noticeable time and disk I/O.

## Public Windows release (installers)

This repo targets **64-bit Windows** only. Maintainers build **MSI packages** with the [WiX Toolset](https://wixtoolset.org/) **through NuGet** (`WixToolset.Sdk`). Only the **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** is required—no separate Inno Setup or WiX install.

| MSI | When to use |
|-----|-------------|
| **Offline** | Same app install surface as online (**Program Files** payload, Start Menu shortcut, ARP branding, finish-page launch option). Difference: includes `models\ggml-large-v3-turbo.bin` inside the MSI, so no Hugging Face access at install time. Requires the model file locally when **building** (not committed to git). |
| **Online** | Same app install surface as offline (**Program Files** payload, Start Menu shortcut, ARP branding, finish-page launch option). Difference: installs **`DownloadModel.cmd`** / **`RunDownloadModelElevated.cmd`** and runs an elevated **WiX QuietExec** download step; `curl` progress is written to the MSI log (for example `msiexec /i PrimeDictate-….msi /l*v install.log`). Requires network access to Hugging Face during install. |

**Build both** (offline requires `.\models\ggml-large-v3-turbo.bin`):

```powershell
.\scripts\Build-Installers.ps1
```

**Online only** (no local model file):

```powershell
.\scripts\Build-Installers.ps1 -Installer Online
```

Outputs: `artifacts\installer\`. Version comes from `Directory.Build.props`.

See [installer/README.md](installer/README.md) for details. Redistribute the GGML model only in compliance with its license/terms.

### GitHub Releases and Webflow links

Tagged pushes that match `vX.Y.Z` keep the workflow artifact upload for CI debugging and also publish installer assets to the matching GitHub Release. If the Release does not exist yet, the workflow creates it first. Release downloads come from **GitHub Releases**, not the temporary workflow artifact ZIP.

- Release page: `https://github.com/CakeRepository/PrimeDictate/releases/tag/vX.Y.Z`
- Direct MSI asset: `https://github.com/CakeRepository/PrimeDictate/releases/download/vX.Y.Z/PrimeDictate-Setup-vX.Y.Z.msi`
- GitHub latest redirect pattern: `https://github.com/CakeRepository/PrimeDictate/releases/latest/download/PrimeDictate-Setup-vX.Y.Z.msi`

Because the MSI filename includes the tag, Webflow should either link to the release page or update the direct MSI URL each time a new release tag is published.

### Tray shell and first-run setup

PrimeDictate now runs as a **WPF tray app** (no console window in normal use):

- **Tray shell**: Notification-area icon with **Open Workspace**, **Settings**, and **Exit** menu items.
- **Tray status colors**: **Ready = Blue**, **Recording = Red**, **Processing = Green**, **Error = Yellow**. Tooltip text follows app state (`Ready`, `Listening`, `Processing transcript`, `Error`).
- **First launch**: If `%LocalAppData%\PrimeDictate\settings.json` is missing or incomplete, a setup window appears to capture hotkey and tray click behavior.
- **Configurable hotkey**: Global hotkey is loaded from saved settings and applied to `GlobalHotkeyListener` at startup (default remains `Ctrl+Shift+Space` until changed).
- **Model override in settings**: Setup window now includes a model file picker that sets a process-local `PRIME_DICTATE_MODEL` override.
- **Preview settings**: Setup window includes the silence auto-commit delay, overlay placement, and optional coding-mode Enter key.
- **Installer continuity**: Offline and online MSIs share one product identity (same MSI name + upgrade family), so installing either flavor upgrades/replaces the other.
- **Installer finish launch**: Both MSI flavors expose **“Launch PrimeDictate when setup completes”** (checked by default), which starts the app after install.

**Publish folder only** (no installer):

```powershell
.\scripts\Publish-Windows.ps1
```

## Build and run

```powershell
cd path\to\PrimeDictate
dotnet run
```

The app starts in the tray. On first launch, complete setup, then focus another application and use your configured hotkey to start dictation. A live transcript appears in the overlay while you speak. PrimeDictate commits after the configured silence delay, or when you press the hotkey again.

**Note:** Stopping a running `dotnet run` (or any running `PrimeDictate.exe`) may be required before `dotnet build` can replace `bin\...\PrimeDictate.exe` on Windows (file lock on the apphost).

### Using dictation reliably

- Keep the **caret** in the field where you want text before starting. The app does not move focus for you.
- Do not click into another application while the tray says **Processing transcript**; if the foreground window changes, injection is skipped for safety.
- Use the overlay as the live feedback surface. The focused editor receives only the final committed transcript.
- For a built-in mouse cue, enable Windows' "show location of pointer when I press CTRL key" setting. PrimeDictate uses that OS feature when available.
- Long monologues are heavier than short phrases because Whisper preview reprocesses snapshots and the final pass processes the full recording. A faster or smaller model helps if this becomes limiting.

## Configuration surface

| Mechanism | Purpose |
|-----------|---------|
| `PRIME_DICTATE_MODEL` | Absolute path to the GGML model file, if not using the default `models/ggml-large-v3-turbo.bin` layout. |
| `WhisperProcessorBuilder` | Language detection and other inference options are set in `WhisperTextInjectionPipeline` (`WithLanguageDetection()`, etc.). |
| User settings + first-run | Stored at `%LocalAppData%\PrimeDictate\settings.json` with `FirstRunCompleted`, dictation hotkey, tray click behavior, model override, optional exclusive mic capture toggle, silence auto-commit delay, overlay placement, and coding-mode Enter toggle. |
| Transcript history | Stored at `%LocalAppData%\PrimeDictate\history.json` with timestamp, transcript text, thread id, delivery status, target display name, optional error, and audio duration metadata. |

## Architecture (high level)

| Area | Technology |
|------|------------|
| Hotkey | SharpHook `SimpleGlobalHook`, keyboard only; gesture loaded from settings and matched on `KeyPressed`. |
| Capture | NAudio `WasapiCapture` + `MediaFoundationResampler` to 16 kHz mono PCM. |
| Live preview | `DictationController.LivePreviewLoopAsync` snapshots the growing PCM buffer, re-runs Whisper for the overlay, and watches recent RMS level for silence. |
| Transcription | `WhisperFactory.FromPath` → `WhisperProcessor` → `ProcessAsync` over an in-memory WAV stream built from PCM; full segment text is assembled per pass. |
| Overlay | `TranscriptionOverlayWindow` is topmost, non-activating, and click-through so the target editor keeps focus. |
| Typing | `WhisperTextInjectionPipeline.TranscribeAsync` builds the final transcript, then `InjectTextToTarget` sends it once with `SimulateTextEntry`; optional coding mode follows with `VcEnter`. |
| Target safety + pointer cue | `WindowsInputHelpers.cs` captures the foreground window at recording start, checks it before injection, and uses Windows Mouse Sonar if enabled. |

### Why not clipboard + Ctrl+V?

An earlier design put the transcript on the clipboard, simulated **Paste**, then restored the previous clipboard. Many applications handle paste **asynchronously**, so the restore often ran **before** the app read the new clipboard, and users saw the **old** clipboard (for example, a recently copied URL). The current design avoids that class of race by not using the clipboard for injection in the first place.

## License

This repository’s application code is provided as in-repo source; follow the licenses of the dependencies (Whisper.net, NAudio, SharpHook, and the GGML model terms from their respective publishers) when redistributing.
