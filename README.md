# PrimeDictate

A locally hosted, global hotkey dictation utility for fast desktop workflows. It captures the default microphone, shows live local transcription in a small overlay, and types the final transcript into the current application after a silence auto-commit or manual stop using SharpHook (no synthetic paste, no clipboard round-trip on the hot path).

## Features

- **Global hotkey**: Configurable global toggle (default `Ctrl+Shift+Space`) to start/stop recording.
- **Tray workspace UI**: Open **Workspace** from the tray icon to browse per-session dictation threads and global runtime logs in a clearer, column-based dashboard layout.
- **Log signal over noise**: Repeated adjacent log entries are collapsed (for example `(... x12)`) and history is capped to keep memory usage predictable.
- **Live preview overlay**: While recording, the app periodically re-transcribes the growing buffer with the selected local backend and shows the current hypothesis in a non-activating overlay.
- **Compact mic overlay**: The default overlay mode keeps a small lower-right microphone visible as a ready/listening indicator, with an option to switch back to the larger transcript panel.
- **Silence auto-commit**: When speech has stopped for the configured delay (default 3 seconds), PrimeDictate stops capture, runs a final transcription pass, and sends the final text once.
- **Transcript history**: Every committed transcript is saved to local history so you can review past dictations, recover text sent to the wrong app, and copy transcript text (with or without metadata).
- **History filters and detail view**: History includes a filter dropdown (**All**, **Injected**, **NotInjected**) plus an expanded detail pane for full transcript and target metadata.
- **History entry points**: Open history from the tray menu, from the settings window, or from the workspace toolbar.
- **Final-only target typing**: The target editor is not mutated while recording. Live corrections stay in the overlay so code editors and IntelliSense are not fighting backspace/retype updates.
- **Coding mode**: Optional setting sends an Enter key immediately after a successful transcript commit.
- **Foreground guard**: The foreground window is captured when recording starts; by default PrimeDictate still skips injection if focus changes before the transcript is ready.
- **Return to original target (optional)**: A dictation setting can deliver the final transcript back to the window that had focus when recording started, first trying a safe direct write to the captured edit control on Windows and otherwise reactivating that window before typing.
- **Built-in pointer cue**: If Windows Mouse Sonar is enabled, PrimeDictate pulses it on recording/processing transitions by tapping Ctrl. It does not draw a custom pointer overlay or change the user's Windows setting.
- **Custom audio earcons**: PrimeDictate can play its own short start/stop tones so you hear when recording begins and when capture hands off to transcription.
- **Audio**: Windows default capture device via NAudio **WASAPI** (`WasapiCapture`), resampled to **16 kHz, 16-bit, mono PCM** for local transcription engines.
- **Mic isolation mode (best effort)**: Optional exclusive-capture setting can block other apps from the mic on supported devices; if exclusive capture fails, PrimeDictate automatically falls back to shared mode and continues dictation.
- **Inference**: [Whisper.net](https://www.nuget.org/packages/Whisper.net) `1.9.0` for GGML Whisper models plus [sherpa-onnx](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx) for Parakeet ONNX models.
- **Injection**: [SharpHook](https://www.nuget.org/packages/SharpHook) `EventSimulator` for Unicode text entry (no synthetic paste, no clipboard round-trip on the hot path).

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or a compatible SDK that can build `net8.0` projects).
- **Windows** is the primary target (WASAPI capture path). Other platforms may require a different capture implementation.
- For CPU Whisper at runtime, the published **Whisper.net** requirements apply (for example, Visual C++ redistributable and instruction-set expectations); see the [Whisper.net readme](https://www.nuget.org/packages/Whisper.net).

## Local transcription backends and model files

PrimeDictate now has a curated backend + model picker during first-run setup and in Settings.

### Whisper

The built-in Whisper catalog currently includes:

| Model | Typical use |
|------|------|
| `Tiny` | Fastest setup and lowest disk usage |
| `Base` | Light Windows laptops and short dictation |
| `Small` | **Recommended** balanced day-to-day editing workflow |
| `Medium` | Better accuracy with more RAM/CPU |
| `Large v3 Turbo` | **Recommended** highest accuracy for polished text on modern PCs |

When you download a Whisper model inside the app, PrimeDictate stores it under **`%LocalAppData%\PrimeDictate\models`** and saves the exact file path in settings.

The runtime resolves model files in this order:

1. Path in the `PRIME_DICTATE_MODEL` environment variable (full path to the `.bin` file).
2. `%LocalAppData%\PrimeDictate\models\...` for managed downloads.
3. `./models\ggml-large-v3-turbo.bin` (relative to the process current working directory, when that path exists).
4. `AppContext.BaseDirectory\models\ggml-large-v3-turbo.bin` (useful if you copy the model next to the published app or install the bundled MSI).
5. A walk upward from the current directory to find `models\ggml-large-v3-turbo.bin` (helps when `dotnet run` uses a `bin\...` working directory but the repository root contains `models\`).

If you prefer to manage files yourself, you can still browse to any Whisper GGML `.bin` model from the [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) collection on Hugging Face.

### Parakeet

PrimeDictate also supports a first non-Whisper backend through **Parakeet + sherpa-onnx**. The current catalog starts with:

| Model | Typical use |
|------|------|
| `Parakeet TDT 0.6B v3` | Try a newer local English STT backend without leaving the PrimeDictate workflow |

Downloaded Parakeet models are stored under **`%LocalAppData%\PrimeDictate\models\parakeet`**. PrimeDictate expects a model folder containing:

- `encoder.int8.onnx`
- `decoder.int8.onnx`
- `joiner.int8.onnx`
- `tokens.txt`

You can either download the managed Parakeet model in-app or browse to an existing extracted model folder.

### Moonshine

PrimeDictate also supports **Moonshine via sherpa-onnx** for another lightweight local English path. The current catalog includes:

| Model | Typical use |
|------|------|
| `Moonshine Base (English)` | Fast local English dictation when you want a smaller non-Whisper backend than Parakeet |

Downloaded Moonshine models are stored under **`%LocalAppData%\PrimeDictate\models\moonshine`**. PrimeDictate expects a model folder containing:

- `preprocess.onnx`
- `encode.int8.onnx`
- `uncached_decode.int8.onnx`
- `cached_decode.int8.onnx`
- `tokens.txt`

You can either download the managed Moonshine model in-app or browse to an existing extracted model folder.

**Example (PowerShell, from repo root)**, downloading the default bundled model manually:

```powershell
New-Item -ItemType Directory -Force -Path "models" | Out-Null
Invoke-WebRequest -Uri "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin" -OutFile "models/ggml-large-v3-turbo.bin"
```

Larger models take longer to download and load. The first transcription after launch may still do noticeable disk I/O while Whisper initializes.

## Public Windows release (installers)

This repo targets **64-bit Windows** only. Maintainers build **MSI packages** with the [WiX Toolset](https://wixtoolset.org/) **through NuGet** (`WixToolset.Sdk`). Only the **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** is required—no separate Inno Setup or WiX install.

| MSI | When to use |
|-----|-------------|
| **Online** | Installs the app under **Program Files** with Start Menu and ARP branding, then downloads models during install through **`DownloadModel.cmd`** / **`RunDownloadModelElevated.cmd`** and an elevated **WiX QuietExec** step. `curl` progress is written to the MSI log (for example `msiexec /i PrimeDictate-….msi /l*v install.log`). |

**Build the online installer**:

```powershell
.\scripts\Build-Installers.ps1
```

Outputs: `artifacts\installer\`. Version comes from `Directory.Build.props`.

See [installer/README.md](installer/README.md) for details. Redistribute the GGML model only in compliance with its license/terms.

### GitHub Releases and Webflow links

Tagged pushes that match `vX.Y.Z` keep the workflow artifact upload for CI debugging and also publish installer assets to the matching GitHub Release. If the Release does not exist yet, the workflow creates it first. Release downloads come from **GitHub Releases**, not the temporary workflow artifact ZIP. If Azure Key Vault signing secrets are unavailable, the release flow still publishes the MSI asset as an unsigned build instead of failing before release upload.

- Release page: `https://github.com/CakeRepository/PrimeDictate/releases/tag/vX.Y.Z`
- Direct MSI asset: `https://github.com/CakeRepository/PrimeDictate/releases/download/vX.Y.Z/PrimeDictate-Setup-vX.Y.Z.msi`
- GitHub latest redirect pattern: `https://github.com/CakeRepository/PrimeDictate/releases/latest/download/PrimeDictate-Setup-vX.Y.Z.msi`

Because the MSI filename includes the tag, Webflow should either link to the release page or update the direct MSI URL each time a new release tag is published.

### Tray shell and first-run setup

PrimeDictate now runs as a **WPF tray app** (no console window in normal use):

- **Tray shell**: Notification-area icon with **Open Workspace**, **Settings**, and **Exit** menu items.
- **Tray status colors**: **Ready = Blue**, **Recording = Red**, **Processing = Green**, **Error = Yellow**. Tooltip text follows app state (`Ready`, `Listening`, `Processing transcript`, `Error`).
- **First launch**: If `%LocalAppData%\PrimeDictate\settings.json` is missing or incomplete, a guided setup window appears with **Welcome**, **Model**, and **Dictation** tabs.
- **Configurable hotkey**: Global hotkey is loaded from saved settings and applied to `GlobalHotkeyListener` at startup (default remains `Ctrl+Shift+Space` until changed).
- **Backend picker + download**: Setup and Settings include curated Whisper, Parakeet, and Moonshine model options, local download progress, and a manual browse fallback.
- **Runtime model switching**: Changing the selected backend or model causes the next transcription session to reload the correct engine automatically.
- **Preview settings**: Setup window includes the overlay style, silence auto-commit delay, optional coding-mode Enter key, PrimeDictate audio cues, and mic capture behavior.
- **Installer continuity**: The online MSI keeps one product identity for clean upgrades.
- **Installer finish launch**: The online MSI exposes **“Launch PrimeDictate when setup completes”** (checked by default), which starts the app after install.

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
| `PRIME_DICTATE_MODEL` | Absolute path to the active GGML Whisper model file. PrimeDictate sets this process-locally from saved settings only when the Whisper backend is selected. |
| `WhisperProcessorBuilder` | Language detection and other inference options are set in `WhisperTextInjectionPipeline` (`WithLanguageDetection()`, etc.). |
| User settings + first-run | Stored at `%LocalAppData%\PrimeDictate\settings.json` with `FirstRunCompleted`, dictation hotkey, selected backend, selected model id, resolved model path, optional exclusive mic capture toggle, overlay style, silence auto-commit delay, return-to-original-target toggle, audio cue toggle, overlay placement, and coding-mode Enter toggle. |
| Transcript history | Stored at `%LocalAppData%\PrimeDictate\history.json` with timestamp, transcript text, thread id, delivery status, target display name, optional error, and audio duration metadata. |

## Architecture (high level)

| Area | Technology |
|------|------------|
| Hotkey | SharpHook `SimpleGlobalHook`, keyboard only; gesture loaded from settings and matched on `KeyPressed`. |
| Capture | NAudio `WasapiCapture` + `MediaFoundationResampler` to 16 kHz mono PCM. |
| Live preview | `DictationController.LivePreviewLoopAsync` snapshots the growing PCM buffer, re-runs the selected local backend for the overlay, and watches recent RMS level for silence. |
| Transcription | Whisper uses `WhisperFactory.FromPath` → `WhisperProcessor` → `ProcessAsync`; Parakeet uses sherpa-onnx `OfflineRecognizer` with the NeMo transducer bundle files. |
| Overlay | `TranscriptionOverlayWindow` is topmost, non-activating, and click-through so the target editor keeps focus. |
| Typing | `WhisperTextInjectionPipeline.TranscribeAsync` builds the final transcript, then `InjectTextToTarget` sends it once; optional coding mode follows with `VcEnter`. |
| Target safety + pointer cue | `WindowsInputHelpers.cs` captures the foreground window and focused control at recording start, can optionally restore that target for final injection, and uses Windows Mouse Sonar if enabled. |

### Why not clipboard + Ctrl+V?

An earlier design put the transcript on the clipboard, simulated **Paste**, then restored the previous clipboard. Many applications handle paste **asynchronously**, so the restore often ran **before** the app read the new clipboard, and users saw the **old** clipboard (for example, a recently copied URL). The current design avoids that class of race by not using the clipboard for injection in the first place.

## License

This repository’s application code is provided as in-repo source; follow the licenses of the dependencies (Whisper.net, NAudio, SharpHook, and the GGML model terms from their respective publishers) when redistributing.
