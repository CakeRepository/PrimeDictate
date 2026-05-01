<div align="center">
  <img src="assets/flowdevs_icon.png" alt="PrimeDictate icon" width="300" />
  <br/>
  <h1>PrimeDictate</h1>
  <a href="https://github.com/CakeRepository/PrimeDictate/actions/workflows/build.yml">
    <img src="https://github.com/CakeRepository/PrimeDictate/actions/workflows/build.yml/badge.svg" alt="Build Status">
  </a>
  <p><b>A locally hosted, global hotkey dictation utility for fast desktop workflows.</b></p>
  <p>It captures the default microphone, shows live local transcription in a small overlay, and types the final transcript into the current application after a silence auto-commit or manual stop using SharpHook (no synthetic paste, no clipboard round-trip on the hot path).</p>
  <br/>
  <img src="assets/overlay_full.png" alt="Live Dictation Overlay" width="600" />
  <br/><br/>
  <img src="assets/workspace.png" alt="Workspace Dashboard" width="600" />
  <br/>
</div>

## Features

- **Global hotkeys**: Configurable global shortcuts for start/stop toggle (default `Ctrl+Shift+Space`), emergency stop/discard (default `Ctrl+Shift+Enter`), and transcript history (default `Ctrl+Shift+H`).
- **Voice commands**: Optional phrases can be detected while dictating. Defaults are `potato farmer` to discard the active capture and `show me the money` to open history; command phrases are removed before final text injection.
- **Tray workspace UI**: Open **Workspace** from the tray icon to browse per-session dictation threads and global runtime logs in a clearer, column-based dashboard layout.
- **AI Prompt Modes (Ollama Integration)**: Automatically rewrite and format your transcripts using a local Ollama instance. Includes dynamic prompt modes (like Bug, Update, and Blog) and injects the active application's context into the LLM for application-aware output.
- **Log signal over noise**: Repeated adjacent log entries are collapsed (for example `(... x12)`) and history is capped to keep memory usage predictable.
- **Live preview overlay**: While recording, the app periodically re-transcribes the growing buffer with the selected local backend and shows the current hypothesis in a non-activating overlay.
- **Compact mic overlay**: The default overlay mode keeps a small lower-right microphone visible as a ready/listening indicator. Clicking it temporarily expands the larger transcript panel without changing your saved default mode.

![Compact Mic Overlay](assets/overlay_compact.png)
- **Silence auto-commit**: When speech has stopped for the configured delay (default 3 seconds), PrimeDictate stops capture, runs a final transcription pass, and sends the final text once.
- **Transcript history**: Every committed transcript is saved to local history so you can review past dictations, recover text sent to the wrong app, and copy transcript text (with or without metadata).
- **Impact stats and achievements**: Successful dictations update local-only productivity stats, including words typed, estimated net time saved, average speaking WPM, last-14-day bars, and milestone notifications.
- **History filters and detail view**: History includes a filter dropdown (**All**, **Injected**, **NotInjected**) plus an expanded detail pane for full transcript and target metadata.
- **History entry points**: Open history from the tray menu, the history hotkey, voice command, the settings window, or the workspace toolbar.
- **Final-only target typing**: The target editor is not mutated while recording. Live corrections stay in the overlay so code editors and IntelliSense are not fighting backspace/retype updates.
- **Coding mode**: Optional setting sends an Enter key immediately after a successful transcript commit.
- **Foreground guard**: The foreground window is captured when recording starts; by default PrimeDictate still skips injection if focus changes before the transcript is ready.
- **Return to original target (optional)**: A dictation setting can deliver the final transcript back to the window that had focus when recording started, first trying a safe direct write to the captured edit control on Windows and otherwise reactivating that window before typing.
- **Built-in pointer cue**: If Windows Mouse Sonar is enabled, PrimeDictate pulses it on recording/processing transitions by tapping Ctrl. It does not draw a custom pointer overlay or change the user's Windows setting.
- **Custom audio earcons**: PrimeDictate can play its own short start/stop tones so you hear when recording begins and when capture hands off to transcription.
- **Launch at login**: Installers enable automatic startup by default so PrimeDictate is ready after a reboot. Silent MSI and Chocolatey installs can opt out.
- **Audio**: Windows default capture device via NAudio **WASAPI** (`WasapiCapture`), resampled to **16 kHz, 16-bit, mono PCM** for local transcription engines.
- **Mic isolation mode (best effort)**: Optional exclusive-capture setting can block other apps from the mic on supported devices; if exclusive capture fails, PrimeDictate automatically falls back to shared mode and continues dictation.
- **Inference**: A shared transcription engine abstraction with Whisper, Parakeet, and Moonshine ONNX models through [sherpa-onnx](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx), plus Whisper.net GGML for GPU/NPU-capable local Whisper runs.
- **Injection**: [SharpHook](https://www.nuget.org/packages/SharpHook) `EventSimulator` for Unicode text entry (no synthetic paste, no clipboard round-trip on the hot path).

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or a compatible SDK that can build `net8.0` projects).
- **Windows** is the primary target (WASAPI capture path). Other platforms may require a different capture implementation.
- The transcription backend uses local model files only. sherpa-onnx backends require ONNX model folders with tokens; Whisper.net uses GGML `.bin` files and optional OpenVINO sidecars.

---

<details>
<summary><b>🛠️ Local transcription backends and model files</b></summary>

## Local transcription backends and model files

PrimeDictate includes a curated backend + model picker during first-run setup and in Settings.

![Model Selection Settings](assets/settings_model.png)

### Whisper

The built-in Whisper catalog currently includes:

| Model | Typical use |
|------|------|
| `Tiny English ONNX` | **Recommended** fastest setup and lowest CPU cost for English dictation |
| `Base English ONNX` | **Recommended** balanced English dictation on CPU |
| `Distil Small English ONNX` | Faster distilled English model for longer sessions |
| `Small English ONNX` | Higher English accuracy with more compute cost |
| `Tiny/Base/Small Multilingual ONNX` | Non-English dictation with the same ONNX folder layout |

When you download a Whisper model inside the app, PrimeDictate stores it under **`%LocalAppData%\PrimeDictate\models\whisper`** and saves the exact folder path in settings.

PrimeDictate expects a Whisper ONNX model folder containing:

- `*-encoder.int8.onnx` or `*-encoder.onnx`
- `*-decoder.int8.onnx` or `*-decoder.onnx`
- `*-tokens.txt`

The runtime checks managed downloads, installed app-local models under `models\whisper`, and repo-local `models\whisper` folders during development.

### Whisper.net (GGML)

PrimeDictate also supports **Whisper.net GGML** models for larger Whisper variants and hardware acceleration. The compute picker only shows locally supported choices:

- **CPU** is always available.
- **GPU** is shown when a supported Whisper.net GPU runtime is present and the machine exposes CUDA or Vulkan.
- **NPU** is shown for supported GGML models with OpenVINO encoder sidecars.

When an older saved setting asks for an unsupported hardware path, PrimeDictate normalizes it at startup. If an installed Whisper.net hardware configuration is available, it prefers that; otherwise it falls back to CPU rather than trying an unsupported provider.

### Parakeet

PrimeDictate also supports **Parakeet ONNX via sherpa-onnx**. The current catalog starts with:

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

PrimeDictate also supports **Moonshine ONNX via sherpa-onnx** for another lightweight local English path. The current catalog includes:

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

### Qualcomm QNN (Experimental)

PrimeDictate now includes an **experimental Qualcomm QNN backend** for **native Windows ARM64** builds. This backend does **not** use sherpa at runtime. Instead, it drives the Moonshine ONNX stages directly through **ONNX Runtime** and can attempt **QNN HTP / Qualcomm NPU** execution when the following are all true:

- the process is running as **Windows ARM64**
- the build contains the QNN runtime assets (`QnnHtp.dll`, `onnxruntime_providers_qnn.dll`, `QnnSystem.dll`)
- the selected Moonshine model folder is present
- the Moonshine stages can be created and run with **CPU fallback disabled**

The Qualcomm backend reuses the same Moonshine model folder layout:

- `preprocess.onnx`
- `encode.int8.onnx`
- `uncached_decode.int8.onnx`
- `cached_decode.int8.onnx`
- `tokens.txt`

Optionally, maintainers can place **QNN-prepared artifacts** under a `qnn` subfolder inside that Moonshine model directory:

- `qnn\preprocess.qdq.onnx`
- `qnn\encode.qdq.onnx`
- `qnn\uncached_decode.qdq.onnx`
- `qnn\cached_decode.qdq.onnx`

If those `qnn` artifacts are present, PrimeDictate prefers them for the Qualcomm backend. If strict validation is **off** and QNN HTP session creation still fails, PrimeDictate falls back to **pure ONNX Runtime CPU** for that backend and logs the actual runtime path. If strict validation is **on**, the backend fails loudly instead of silently using CPU.

Maintainer knobs for the experimental Qualcomm backend:

- `PRIMEDICTATE_QNN_STRICT=1`: require QNN HTP session creation and inference with CPU fallback disabled
- `PRIMEDICTATE_QNN_CONTEXT_CACHE=0|1`: disable or enable QNN context caching (default `1`)
- `PRIMEDICTATE_QNN_CONTEXT_EMBED=0|1`: control QNN context embed mode (default `1`)
- `PRIMEDICTATE_QNN_PROFILE=basic|detailed|optrace`: optional QNN profiling, off by default
- `PRIMEDICTATE_QNN_PROFILE_DIR=<path>`: where profiling CSV output should be written

**Important:** the current managed Moonshine download is still the baseline Moonshine folder, not a pre-validated QNN bundle. The Qualcomm backend can prove strict QNN HTP activation only when the selected model stages actually load and run on QNN with CPU fallback disabled.

**Example (PowerShell, from repo root)**, downloading the default bundled model manually:

```powershell
New-Item -ItemType Directory -Force -Path "models\whisper" | Out-Null
Invoke-WebRequest -Uri "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-base.en.tar.bz2" -OutFile "models\whisper\sherpa-onnx-whisper-base.en.tar.bz2"
tar -xjf "models\whisper\sherpa-onnx-whisper-base.en.tar.bz2" -C "models\whisper"
```

Larger models take longer to download and load. The first transcription after launch may still do noticeable disk I/O while the ONNX runtime initializes.

</details>

<details>
<summary><b>📦 Public Windows release (installers)</b></summary>

## Public Windows release (installers)

Public installers target **64-bit x64 Windows** only. Maintainers build **MSI packages** with the [WiX Toolset](https://wixtoolset.org/) **via NuGet** (`WixToolset.Sdk`). Only the **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** is required—no separate Inno Setup or WiX install.

| MSI | When to use |
|-----|-------------|
| **Online** | Installs the app under **Program Files** with Start Menu and ARP branding, then downloads models during install through **`DownloadModel.cmd`** / **`RunDownloadModelElevated.cmd`** and an elevated **WiX QuietExec** step. `curl` progress is written to the MSI log (for example `msiexec /i PrimeDictate-….msi /l*v install.log`). |

**Build the online installer**:

```powershell
.\scripts\Build-Installers.ps1
```

Outputs: `artifacts\installer\`. Version comes from `Directory.Build.props`.

See [installer/README.md](installer/README.md) for details. Redistribute ONNX model files only in compliance with their license/terms.

### Maintainer release commands

Use this from a clean `main` worktree when publishing a new release. Replace `4.3.1` with the next version.

```powershell
$Version = "4.3.1"

git status --short --branch
# Update Directory.Build.props to $Version before committing.
dotnet build -c Release

git add -A
git commit -m "release: v$Version"

git ls-remote --tags origin "refs/tags/v$Version"
git tag -a "v$Version" -m "v$Version"

git push origin main
git push origin "v$Version"

git status --short --branch
```

If `git ls-remote` prints an existing tag, do not recreate it; choose the correct next version or inspect the existing release first.

### GitHub Releases and Webflow links

Tagged pushes that match `vX.Y.Z` keep the workflow artifact upload for CI debugging and also publish installer assets to the matching GitHub Release. If the Release does not exist yet, the workflow creates it first. Release downloads come from **GitHub Releases**, not the temporary workflow artifact ZIP. If Azure Key Vault signing secrets are unavailable, the release flow still publishes assets as unsigned builds instead of failing before release upload.

- Release page: `https://github.com/CakeRepository/PrimeDictate/releases/tag/vX.Y.Z`
- Direct MSI asset: `https://github.com/CakeRepository/PrimeDictate/releases/download/vX.Y.Z/PrimeDictate-Setup-vX.Y.Z.msi`
- Latest release page: `https://github.com/CakeRepository/PrimeDictate/releases/latest`
- Chocolatey package asset: `https://github.com/CakeRepository/PrimeDictate/releases/download/vX.Y.Z/primedictate.X.Y.Z.nupkg`

Because the MSI filename includes the tag, Webflow should either link to the release page/latest page or update the direct MSI URL each time a new release tag is published.

### Chocolatey release alignment

Chocolatey packaging is part of the same `vX.Y.Z` tag release flow so MSI, docs, and Chocolatey stay aligned.

- Chocolatey package id: `primedictate`
- Chocolatey package source/repo: `https://github.com/CakeRepository/PrimeDictate`
- Product overview page: `https://www.flowdevs.io/portfolio/project/primedictate-local-ai-dictation-app`
- Official release downloads: `https://github.com/CakeRepository/PrimeDictate/releases`

The tag-triggered workflow pushes to Chocolatey only when `CHOCO_API_KEY` is present. Without that secret, the workflow still publishes release assets to GitHub and logs that Chocolatey push was skipped.

For moderation retries, maintainers can repack and push locally with the same version after fixing package metadata/files. See `installer/README.md` for the local repack checklist and required moderation files.

### Silent install and update commands (Windows)

- MSI install (silent): `msiexec /i PrimeDictate-Setup-vX.Y.Z.msi /qn /norestart`
- MSI install without launch at login: `msiexec /i PrimeDictate-Setup-vX.Y.Z.msi LAUNCHATLOGIN=0 /qn /norestart`
- MSI upgrade (silent): `msiexec /i PrimeDictate-Setup-vX.Y.Z.msi REINSTALL=ALL REINSTALLMODE=vomus /qn /norestart`
- MSI uninstall (silent): `msiexec /x PrimeDictate-Setup-vX.Y.Z.msi /qn /norestart`
- Chocolatey install (silent by default): `choco install primedictate -y`
- Chocolatey install without launch at login: `choco install primedictate -y --params "'/NoLaunchAtLogin'"`
- Chocolatey upgrade (silent): `choco upgrade primedictate -y`
- Chocolatey uninstall (silent): `choco uninstall primedictate -y`

### Tray shell and first-run setup

PrimeDictate now runs as a **WPF tray app** (no console window in normal use):

- **Tray shell**: Notification-area icon with **Open Workspace**, **Settings**, and **Exit** menu items.
- **Tray status colors**: **Ready = Blue**, **Recording = Red**, **Processing = Green**, **Error = Yellow**. Tooltip text follows app state (`Ready`, `Listening`, `Processing transcript`, `Error`).
- **First launch**: If `%LocalAppData%\PrimeDictate\settings.json` is missing or incomplete, a guided setup window appears with **Welcome**, **Model**, **Dictation**, **Replacements**, and **Impact** tabs.
- **Configurable shortcuts**: Global toggle, emergency stop, and history shortcuts are loaded from saved settings and applied to `GlobalHotkeyListener` at startup. Voice command phrases are configurable from the same simplified Shortcuts tab.
- **Backend picker + download**: Setup and Settings include curated Whisper, Parakeet, and Moonshine model options, local download progress, and a manual browse fallback.
- **Experimental ARM64 QNN path**: Native `win-arm64` builds can expose an experimental Qualcomm QNN backend that drives Moonshine through ONNX Runtime with explicit QNN-versus-CPU diagnostics.
- **Runtime model switching**: Changing the selected backend or model causes the next transcription session to reload the correct engine automatically.
- **Preview settings**: Setup window includes the overlay style, silence auto-commit delay, optional coding-mode Enter key, PrimeDictate audio cues, and mic capture behavior.
- **Impact dashboard**: Settings includes a local stats tab with productivity cards, a 14-day words chart, and milestone achievements.
- **Installer continuity**: The online MSI keeps one product identity for clean upgrades.
- **Installer finish launch**: The online MSI exposes **“Launch PrimeDictate when setup completes”** (checked by default), which starts the app after install.
- **Launch at login**: MSI installs add a Windows Run value by default. Use `LAUNCHATLOGIN=0` for silent MSI installs or Chocolatey `/NoLaunchAtLogin` when you do not want PrimeDictate to start when users sign in.

**Publish folder only** (no installer):

```powershell
.\scripts\Publish-Windows.ps1
```

</details>

<details>
<summary><b>💻 Build and run</b></summary>

## Build and run

```powershell
cd path\to\PrimeDictate
dotnet run
```

The app starts in the tray. On first launch, complete setup, then focus another application and use your configured hotkey to start dictation. A live transcript appears in the overlay while you speak. PrimeDictate commits after the configured silence delay or when you press the start/stop toggle again. The emergency stop shortcut and stop phrase discard the active capture without typing text.

**Note:** Stopping a running `dotnet run` (or any running `PrimeDictate.exe`) may be required before `dotnet build` can replace `bin\...\PrimeDictate.exe` on Windows (file lock on the apphost).

### Using dictation reliably

- Keep the **caret** in the field where you want text before starting. The app does not move focus for you.
- Do not click into another application while the tray says **Processing transcript**; if the foreground window changes, injection is skipped for safety.
- Use the overlay as the live feedback surface. The focused editor receives only the final committed transcript.
- For a built-in mouse cue, enable Windows' "show location of pointer when I press CTRL key" setting. PrimeDictate uses that OS feature when available.
- Long monologues are heavier than short phrases because Whisper preview reprocesses snapshots and the final pass processes the full recording. A faster or smaller model helps if this becomes limiting.

</details>

<details>
<summary><b>⚙️ Configuration surface & Architecture</b></summary>

## Configuration surface

| Mechanism | Purpose |
|-----------|---------|
| `--enable-launch-at-login` / `--disable-launch-at-login` | Command line switches that add or remove the Windows Run entry for PrimeDictate. Machine-wide entries require an elevated shell to disable. |
| User settings + first-run | Stored at `%LocalAppData%\PrimeDictate\settings.json` with `FirstRunCompleted`, dictation/stop/history hotkeys, optional voice command phrases, selected backend, selected model id, resolved model path, optional exclusive mic capture toggle, overlay style, silence auto-commit delay, return-to-original-target toggle, audio cue toggle, overlay placement, coding-mode Enter toggle, and baseline typing speed for impact estimates. |
| `PRIMEDICTATE_QNN_*` env vars | Maintainer and validation controls for the experimental Qualcomm QNN backend, including strict no-CPU-fallback mode, context caching, and optional QNN profiling output. |

![Dictation Settings](assets/settings_dictation.png)

| Transcript history | Stored at `%LocalAppData%\PrimeDictate\history.json` with timestamp, transcript text, thread id, delivery status, target display name, optional error, and audio duration metadata. |
| Impact stats | Stored at `%LocalAppData%\PrimeDictate\stats.json` with local aggregate word counts, audio duration, daily buckets, and unlocked achievements. |

## Architecture (high level)

| Area | Technology |
|------|------------|
| Hotkey | SharpHook `SimpleGlobalHook`, keyboard only; toggle, emergency stop, and history gestures are loaded from settings and matched on `KeyPressed`. |
| Capture | NAudio `WasapiCapture` + `MediaFoundationResampler` to 16 kHz mono PCM. |
| Live preview | `DictationController.LivePreviewLoopAsync` snapshots the growing PCM buffer, re-runs the selected local backend for the overlay, and watches recent RMS level for silence. |
| Transcription | `TranscriptionEngineHost` selects and owns the active engine. Whisper, Parakeet, and Moonshine use ONNX models through sherpa-onnx `OfflineRecognizer`. The experimental Qualcomm backend drives Moonshine stages directly with ONNX Runtime and can attempt strict QNN HTP execution on native Windows ARM64. |
| Overlay | `TranscriptionOverlayWindow` is topmost, non-activating, and click-through so the target editor keeps focus. |
| Typing | `WhisperTextInjectionPipeline.TranscribeAsync` builds the final transcript, then `InjectTextToTarget` sends it once; optional coding mode follows with `VcEnter`. |
| Target safety + pointer cue | `WindowsInputHelpers.cs` captures the foreground window and focused control at recording start, can optionally restore that target for final injection, and uses Windows Mouse Sonar if enabled. |

### Experimental Qualcomm QNN maintainer workflow

Build the app and native ARM64 publish output as usual:

```powershell
dotnet build PrimeDictate.sln
.\scripts\Publish-Windows.ps1 -RuntimeIdentifier win-arm64
```

To prepare Moonshine artifacts for the experimental QNN backend, use the maintainer-only scaffold in [scripts/qnn/quantize_moonshine_for_qnn.py](scripts/qnn/quantize_moonshine_for_qnn.py). It expects stage-specific calibration data in `.npz` files and writes the generated QDQ artifacts into the Moonshine model folder's `qnn` subdirectory.

To run the built-in backend smoke validation after a build:

```powershell
$moonshineDir = Join-Path $env:LOCALAPPDATA 'PrimeDictate\models\moonshine\sherpa-onnx-moonshine-base-en-int8'
dotnet run -- --qnn-smoke $moonshineDir Cpu
```

If you want the JSON result persisted to a file instead of relying on stdout, add an optional trailing output path:

```powershell
$output = Join-Path $env:TEMP 'primedictate-qnn-smoke.json'
dotnet run -- --qnn-smoke $moonshineDir Cpu $output
```

To run the strict Qualcomm QNN proof path with CPU fallback disabled:

```powershell
$moonshineDir = Join-Path $env:LOCALAPPDATA 'PrimeDictate\models\moonshine\sherpa-onnx-moonshine-base-en-int8'
dotnet run -- --qnn-proof $moonshineDir true
```

PrimeDictate also includes a Whisper-specific Qualcomm QNN compatibility harness. This validates direct encoder/decoder session creation only; it does not yet run managed Whisper feature extraction or the autoregressive decoder loop:

```powershell
$whisperDir = Join-Path $env:LOCALAPPDATA 'PrimeDictate\models\whisper\sherpa-onnx-whisper-small.en'
dotnet run -- --qnn-whisper-smoke $whisperDir Cpu
dotnet run -- --qnn-whisper-proof $whisperDir true
```

Like the Moonshine commands, you can add an optional trailing output path to persist the JSON result to a file instead of relying on stdout.

If strict validation passes, the result reports that QNN HTP session creation and inference succeeded with CPU fallback disabled. If it fails, PrimeDictate should not be treated as NPU-validated on that model/device combination.

### Why not clipboard + Ctrl+V?

An earlier design put the transcript on the clipboard, simulated **Paste**, then restored the previous clipboard. Many applications handle paste **asynchronously**, so the restore often ran **before** the app read the new clipboard, and users saw the **old** clipboard (for example, a recently copied URL). The current design avoids that class of race by not using the clipboard for injection in the first place.

</details>

---

## License

This repository’s application code is provided as in-repo source; follow the licenses of the dependencies (sherpa-onnx, NAudio, SharpHook, and the ONNX model terms from their respective publishers) when redistributing.

## About FlowDevs & The Author

**PrimeDictate** is built by **Justin Trantham**, Co-Founder and Prime Automator at [FlowDevs](https://flowdevs.io).

Founded in 2023, FlowDevs bridges the gap between business goals and scalable tech. We stop you from running your company on spreadsheets by building custom internal tools, client portals, and Power Platform solutions that actually scale. We specialize in workflow automation, integrations, and internal apps for Minnesota businesses.

- [Visit FlowDevs.io](https://flowdevs.io)
- [Meet the Team](https://flowdevs.io/team)
- [Book a Discovery Call](https://booking.flowdevs.io)
