# Windows installers (WiX / MSI)

Installers are **64-bit Windows Installer packages (.msi)** built with the [WiX Toolset](https://wixtoolset.org/) **through NuGet** (`WixToolset.Sdk`). You only need the **.NET 8 SDK**; you do **not** install WiX separately.

The **online** MSI references **`WixToolset.Util.wixext`** (elevated model download). See that package’s [license / terms](https://github.com/wixtoolset/wix/blob/main/OSMFEULA.txt) if you redistribute installers.

| MSI | Contents |
|-----|----------|
| **Online** (`PrimeDictate-*-Windows-Online.msi`) | App under `Program Files\PrimeDictate`, **Start Menu** shortcut, launch-at-login Run value by default, and **Add/Remove Programs** icon. After files are installed, a **deferred QuietExec** custom action (LocalSystem) runs **`DownloadModel.cmd`** so **`curl`** can write under Program Files. Console output (including **`curl --progress-bar`**) is captured in the MSI log, not in a separate window. **`RunDownloadModelElevated.cmd`** is included if you prefer a visible UAC/console flow. |

## Installer UX

- **Online MSI**: Uses WiX UI with **“Launch PrimeDictate when setup completes”** (checked by default). If checked, setup launches `[INSTALLFOLDER]PrimeDictate.exe` from the finish dialog after the deferred model download custom action runs in execute sequence.
- **Launch at login**: Setup installs a Windows Run value by default so PrimeDictate runs after users sign in. Silent installs can opt out with `LAUNCHATLOGIN=0`.
- **First-run app entry**: Launching at install finish lands users in the app’s first-run setup when `%LocalAppData%\PrimeDictate\settings.json` is not yet completed.
- **Branding continuity**: ARP metadata, MSI names, Start Menu shortcut text, and finish-page launch prompt now align with the app’s branded status language (**Ready=Blue, Recording=Red, Error=Yellow**).
- **Upgrade continuity**: The online MSI keeps the existing product identity (`Name` + `UpgradeCode`) for clean upgrades.
- **Language**: The installer is pinned to `en-US` UI resources for consistent English setup dialogs.

## Prerequisites (maintainer)

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build

From the repository root:

```powershell
.\scripts\Build-Installers.ps1
```

Outputs are copied to `artifacts\installer\`. Intermediate build outputs live under `installer\wix\online\bin\`.

Release builds also generate a Chocolatey package at `artifacts\installer\primedictate.<version>.nupkg` when `choco.exe` is available.

## Chocolatey publishing

PrimeDictate publishes a Chocolatey package from the same tagged release pipeline as the MSI (`vX.Y.Z` tags only).

- Package id: `primedictate`
- Community page: `https://community.chocolatey.org/packages/primedictate`
- Source/release assets: `https://github.com/CakeRepository/PrimeDictate/releases`
- Product overview/docs: `https://www.flowdevs.io/portfolio/project/primedictate-local-ai-dictation-app`

### Maintainer flow (recommended)

1. Ensure `Directory.Build.props` has the intended release version (for example `3.2.0`).
2. Push release commit to `main`.
3. Create and push tag `v<version>` (for example `v3.2.0`).
4. The `build.yml` tag run will:
   - build publish payload + online MSI,
   - build `primedictate.<version>.nupkg`,
   - attach both to the matching GitHub Release,
   - push to Chocolatey when `CHOCO_API_KEY` is configured.

If `CHOCO_API_KEY` is missing, the workflow still builds assets and publishes to GitHub Releases, but skips Chocolatey push.

### Local maintainer repack/push (no GitHub Actions)

Use this when Chocolatey moderation asks for fixes on the same package version.

1. Download the target release MSI (for example `v3.1.2`) from GitHub Releases.
2. Replace `installer\chocolatey\tools\PrimeDictate-Online.msi` with that MSI.
3. Ensure `installer\chocolatey\tools\LICENSE.txt` and `installer\chocolatey\tools\VERIFICATION.txt` are present and match the exact version/hash being submitted.
4. Run:

```powershell
choco pack .\installer\chocolatey\primedictate.nuspec --version <version>
choco push .\installer\chocolatey\primedictate.<version>.nupkg --source https://push.chocolatey.org/ --api-key <your-api-key>
```

Security note: never commit API keys, never place them in repository files, and rotate any key that was ever shared outside your secure secret storage.

### Chocolatey moderation checklist

Before pushing a moderated rebuild, verify:

- `iconUrl` resolves publicly (HTTP 200) and points to an existing image file.
- `licenseUrl`, `packageSourceUrl`, and `releaseNotes` are valid URLs.
- Required bundled-binary docs exist in `tools\` (`LICENSE.txt`, `VERIFICATION.txt`).
- `VERIFICATION.txt` release URL and SHA256 exactly match the MSI shipped in `tools\PrimeDictate-Online.msi`.

## Silent install and upgrade

- Install: `msiexec /i PrimeDictate-<version>-Windows-Online.msi /qn /norestart`
- Install without launch at login: `msiexec /i PrimeDictate-<version>-Windows-Online.msi LAUNCHATLOGIN=0 /qn /norestart`
- Upgrade: `msiexec /i PrimeDictate-<version>-Windows-Online.msi REINSTALL=ALL REINSTALLMODE=vomus /qn /norestart`
- Uninstall: `msiexec /x PrimeDictate-<version>-Windows-Online.msi /qn /norestart`
- Chocolatey install: `choco install primedictate -y`
- Chocolatey install without launch at login: `choco install primedictate -y --params "'/NoLaunchAtLogin'"`
- Chocolatey upgrade: `choco upgrade primedictate -y`
- Chocolatey uninstall: `choco uninstall primedictate -y`

## Layout

| Path | Role |
|------|------|
| `wix/shared/AppPayload.wxs` | `Program Files\PrimeDictate` tree and harvested publish payload |
| `wix/shared/Branding.wxs` | ARP icon + common Add/Remove Programs metadata |
| `wix/shared/StartMenuShortcuts.wxs` | Shared Start Menu shortcut component used by the online installer |
| `wix/shared/LaunchAtLogin.wxs` | Optional launch-at-login Run value controlled by `LAUNCHATLOGIN` |
| `wix/online/` | Online package, **Util** QuietExec download, helper `.cmd` scripts |
| `wix/assets/PrimeDictate.ico` | App + installer icon (also **`ApplicationIcon`** on `PrimeDictate.exe`) |
| `wix/assets/DownloadModel.cmd` | Curl download used by QuietExec and by the elevated helper |
| `wix/assets/RunDownloadModelElevated.cmd` | Optional manual re-download with visible UAC |

## Version

`Package` / MSI product version uses `Directory.Build.props` (`Version`) with a fourth field `.0` for Windows Installer (for example `1.0.0` → `1.0.0.0`).

## End-user notes

- Install is **per machine** (`Scope="perMachine"`) under **Program Files**.
- **Visual C++ Redistributable** may be required for Whisper native code; see [Whisper.net](https://www.nuget.org/packages/Whisper.net).
- Downloaded models remain subject to their publishers’ terms; redistribute only in compliance with those terms.
