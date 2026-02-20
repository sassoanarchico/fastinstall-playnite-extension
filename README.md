<div align="center">

# FastInstall

**Playnite extension that manages games on archive drives and installs them to fast SSDs**

[![Version](https://img.shields.io/badge/version-1.3.5-blue.svg)]()
[![Platform](https://img.shields.io/badge/platform-Playnite%2010+-purple.svg)]()
[![Framework](https://img.shields.io/badge/.NET-Framework%204.8-green.svg)]()
[![License](https://img.shields.io/badge/license-MIT-lightgrey.svg)](LICENSE)

Store your game collection on a slow archive HDD, install to a fast SSD when you want to play, and uninstall safely — all from the Playnite library. Supports local folders, compressed archives, Google Drive, and emulator integration.

</div>

---

## Table of Contents

- [Features](#features)
- [Screenshots](#screenshots)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Settings](#settings)
- [Project Structure](#project-structure)
- [Development](#development)
- [Roadmap](#roadmap)
- [Known Issues](#known-issues)
- [Changelog](#changelog)
- [License](#license)

---

## Features

### Core Functionality
| Feature | Description |
|---------|-------------|
| **Archive → SSD Install** | Copy game folders from archive HDD to fast SSD with one click |
| **Multiple Folder Pairs** | Configure multiple source/destination path combinations |
| **Safe Uninstall** | Remove installed files from SSD without touching the archive |
| **Auto Game Detection** | Scans source directories and detects games automatically |
| **Stable Game IDs** | Consistent ID generation prevents duplicate library entries |
| **Path Normalization** | Handles different path formats consistently |

### Installation Management
| Feature | Description |
|---------|-------------|
| **Background Installation** | Non-blocking progress window — keep using Playnite during installs |
| **Download Manager** | View and manage all active, queued, and paused installations |
| **Installation Queue** | Multiple installs are queued and processed automatically |
| **Parallel Installs** | Configure 1–10 simultaneous installations |
| **Pause / Resume** | Pause ongoing installations and resume them later |
| **Queue Priority** | Set Low, Normal, or High priority for each job |
| **Conflict Resolution** | Choose how to handle existing installations (Ask, Overwrite, Skip) |

### Progress Tracking
| Feature | Description |
|---------|-------------|
| **Real-time Progress Bar** | Percentage, bytes copied / total size |
| **Transfer Speed** | Live MB/s display |
| **Time Estimates** | Elapsed time and ETA |
| **File Counter** | Current file name and progress count |
| **Auto-close Window** | Closes automatically after installation completes |

### Archive Support
| Feature | Description |
|---------|-------------|
| **Compressed Files** | Automatically extract ZIP, RAR, 7Z, and multi-part archives |
| **7-Zip Integration** | Configure 7-Zip path in settings with download button |
| **Disk Space Check** | Verifies available space before installation starts |
| **Disk Space Preview** | Shows required vs available space in notifications |
| **Integrity Verification** | Verifies all files after copy by comparing file sizes |

### Google Drive Integration
| Feature | Description |
|---------|-------------|
| **Cloud Game Library** | Browse and install games stored on Google Drive |
| **API Key Authentication** | Uses Google Drive API v3 with your own API key |
| **Virus Scan Bypass** | Handles Google's large-file virus scan confirmation automatically |
| **Cloud Download Manager** | Dedicated queue for cloud downloads with progress tracking |
| **Archive Extraction** | Downloads are extracted automatically if compressed |

### Platform & Emulator Support
| Feature | Description |
|---------|-------------|
| **Platform Detection** | Automatic platform detection based on folder structure |
| **PS3 Game Detection** | Recognizes PS3_GAME folder structure and EBOOT.BIN |
| **Emulator Selection** | Configure emulator per source folder |
| **Profile Selection** | Choose specific emulator profiles |
| **Dynamic Profile Filtering** | Shows only profiles matching the selected emulator |
| **Game Launching** | Launch games via configured emulator with proper arguments |

### Interface
| Feature | Description |
|---------|-------------|
| **Tabbed Settings** | Local Sources, Google Drive, and About tabs |
| **DataGrid Configuration** | Inline editing of folder pairs with platform/emulator columns |
| **Playnite Notifications** | Status updates for install start, complete, and errors |
| **Localization** | English and Italian — auto-selected from Playnite language |

---

## Screenshots

> The settings view shows a table of source/destination folder pairs with platform and emulator configuration.
> The installation progress window displays real-time speed, ETA, and file count.
> The download manager lists all active, queued, and paused installations.

---

## Installation

### Quick Method (.pext file)

1. Download `FastInstall_v1.3.5.pext` from the [Releases](https://github.com/sassoanarchico/fastinstall-playnite-extension/releases) page
2. In Playnite: **Menu → Add-ons... → Install from file**
3. Select the downloaded `.pext` file
4. **Restart Playnite**
5. FastInstall appears as a library source in the sidebar

### Manual Installation (developers)

```powershell
# Clone the repository
git clone https://github.com/sassoanarchico/fastinstall-playnite-extension.git
cd fastinstall-playnite-extension

# Build and package
.\build.ps1
```

Or copy the compiled folder to `%AppData%\Playnite\Extensions\FastInstall\`.

---

## Quick Start

### Configuring Folder Pairs

1. Go to **Playnite → Add-ons → FastInstall → Settings**
2. In the **Local Sources** tab, click **Add Row**
3. Configure:
   - **Source Path** — Your archived games folder (slow HDD, e.g. `D:\Games\Archive`)
   - **Destination Path** — Where games should be copied (fast SSD, e.g. `C:\Games`)
   - **Platform** — Platform for auto-detection and play actions
   - **Emulator** — (Optional) Emulator configured in Playnite
   - **Profile** — (Optional) Specific emulator profile to use
4. Click **Save** — games are detected automatically on the next library update

### Installing a Game

1. Find the game in your Playnite library (FastInstall games show as "not installed")
2. Click **Install** (or right-click → Install)
3. The installation progress window appears with:
   - Progress bar and percentage
   - Transfer speed (MB/s) and ETA
   - Pause, Resume, and Cancel buttons
4. Once complete, the game is marked as installed and ready to play

### Playing a Game

- **PC Games** — Double-click to launch, or Playnite opens the game folder
- **Emulator Games** — Launches via the configured emulator with the correct arguments

### Uninstalling a Game

1. Right-click the game → **Uninstall**
2. The installed copy is removed from the SSD
3. The archive copy on the HDD is **never touched**

### Google Drive Setup

1. Go to **Settings → Google Drive** tab
2. Enter your Google Drive **API Key** (from Google Cloud Console)
3. Add a cloud source:
   - **Folder ID** — The Google Drive folder ID (from the URL)
   - **Destination Path** — Local path where games will be downloaded
   - **Platform** — Platform for detected games
4. Games from Google Drive appear in your library and can be installed like local games

---

## Settings

Accessible from **Playnite → Add-ons → FastInstall → Settings**.

### Local Sources Tab

| Column | Description |
|--------|-------------|
| Source Path | Archived games folder (slow HDD) |
| Destination Path | Installation target (fast SSD) |
| Platform | Platform assigned to detected games |
| Emulator | Emulator for this folder's games |
| Profile | Specific emulator profile |

### Google Drive Tab

| Setting | Description |
|---------|-------------|
| API Key | Your Google Drive API v3 key |
| Folder ID | Google Drive folder to scan |
| Destination Path | Local download destination |
| Platform | Platform for cloud games |

### General Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Parallel Installs | `No` | Enable multiple simultaneous installations |
| Max Parallel | `2` | Number of concurrent installs (1–10) |
| Conflict Resolution | `Ask` | How to handle existing files (Ask, Overwrite, Skip) |
| 7-Zip Path | — | Path to `7z.exe` for archive extraction |

---

## Project Structure

```
fastinstall-playnite-extension/
├── FastInstallPlugin.cs            # Entry point: LibraryPlugin, game detection, ID generation
├── FastInstallClient.cs            # Install/Uninstall/Play controllers for Playnite SDK
├── FastInstallSettings.cs          # Settings model + MVVM ViewModel, legacy migration
├── extension.yaml                  # Playnite manifest (v1.3.5, GameLibrary)
│
├── FileCopyHelper.cs               # File copy with progress, integrity verification, disk space
├── ArchiveHelper.cs                # Archive detection and 7-Zip extraction (.zip/.rar/.7z/.001)
├── BackgroundInstallManager.cs     # Singleton: install queue with parallel/pause/resume support
│
├── ICloudStorageProvider.cs        # Cloud storage abstraction + DTOs
├── GoogleDriveProvider.cs          # Google Drive API v3: file listing, download, virus scan bypass
├── CloudDownloadManager.cs         # Singleton: cloud download queue with progress tracking
│
├── Views/
│   ├── FastInstallSettingsView.xaml/.cs         # Settings UI (3 tabs)
│   ├── InstallationProgressWindow.xaml/.cs      # Progress window (speed, ETA, pause/resume)
│   └── DownloadManagerWindow.xaml/.cs           # Download manager (active/queued/paused jobs)
│
├── Localization/
│   ├── LocalizationConverters.cs               # WPF IValueConverter for localized enums
│   ├── LocalizedStringExtension.cs             # XAML markup extension with 3-level fallback
│   ├── en_US.xaml                              # English strings (~200+ keys)
│   └── it_IT.xaml                              # Italian strings
│
├── Properties/
│   └── AssemblyInfo.cs             # Assembly metadata
│
├── build.ps1                       # MSBuild + .pext packaging script
├── FastInstall.csproj              # .NET Framework 4.8 project file
├── FastInstall.sln                 # Visual Studio solution
└── checklog.txt                    # Historical changelog (v0.1.5 → v1.1.1)
```

---

## Development

### Prerequisites

- Visual Studio 2022+ or VS Code with C# extension
- .NET Framework 4.8 Developer Pack
- Playnite installed (for `Playnite.SDK.dll`)
- (Optional) 7-Zip for archive extraction testing

### Building

```powershell
cd fastinstall-playnite-extension
.\build.ps1
```

The script:
1. Finds `Playnite.SDK.dll` in `%LOCALAPPDATA%\Playnite`
2. Compiles via MSBuild in Release mode
3. Copies required files (DLL, extension.yaml, icon, localization)
4. Creates the `.pext` file with the version from `extension.yaml`

### Architecture

The extension follows a **Plugin → Controller → Manager → Helper** layered pattern:

1. **Plugin** (`FastInstallPlugin`) — Entry point, game detection, settings access
2. **Controllers** (`FastInstallClient.cs`) — Playnite SDK install/uninstall/play handlers
3. **Managers** (singletons) — Job queues with concurrency control (`BackgroundInstallManager`, `CloudDownloadManager`)
4. **Helpers** (static) — Low-level operations (`FileCopyHelper`, `ArchiveHelper`)
5. **Providers** (`ICloudStorageProvider` → `GoogleDriveProvider`) — Cloud storage abstraction

**Local install flow**:
1. `GetGames()` scans source folders → creates `GameMetadata` per detected game
2. User clicks Install → `FastInstallController` → `BackgroundInstallManager.QueueInstallation()`
3. Queue processes: disk space check → file copy with progress → integrity verification → archive extraction (if needed) → mark installed

**Cloud install flow**:
1. `GetCloudGames()` queries Google Drive API → creates `GameMetadata` per cloud file
2. User clicks Install → `CloudInstallController` → `CloudDownloadManager.QueueDownload()`
3. Queue processes: API download to temp file → extract if archive → copy to destination → mark installed

### How Game Detection Works

1. Each configured source folder is scanned for subdirectories
2. `DetectGameType()` examines the folder contents:
   - PS3: looks for `PS3_GAME/` directory or `EBOOT.BIN`
   - Archives: checks for `.zip`, `.rar`, `.7z`, `.001` files
   - Default: treats folder as a PC game
3. `GenerateGameId()` creates a deterministic hash from source path + game name
4. Platform and emulator are assigned from the folder configuration

---

## Roadmap

### High Priority
- [ ] **Fix `async void` in `CloudDownloadManager.ProcessQueue()`** — Change to `async Task` to prevent unhandled exception crashes
- [ ] **Fix SemaphoreSlim race condition** — `SetMaxParallelInstalls()` disposes semaphore while active installs may hold it
- [ ] **Fix version mismatch** — Unify `extension.yaml` (1.3.5), `PluginVersion` const (1.3.4), and `AssemblyInfo.cs` (1.2.1.0)
- [ ] **Fix ArchiveHelper deadlock** — Read stdout/stderr before `WaitForExit()` to prevent pipe buffer deadlocks
- [ ] **Implement `SetMaxParallelDownloads()`** — Currently a no-op; users changing the setting see no effect
- [ ] **Add temp file cleanup on startup** — Orphaned multi-GB temp files from crashed downloads are never cleaned up

### Medium Priority
- [ ] **Replace regex JSON parsing** — Use `JavaScriptSerializer` or proper JSON parser in `GoogleDriveProvider`
- [ ] **Add download retry on network failure** — Currently a single TCP error fails the entire multi-GB download
- [ ] **Add file copy retry on transient I/O errors** — Single `IOException` fails the whole operation
- [ ] **Fix DownloadManagerWindow flicker** — Update items in-place instead of clearing and rebuilding every second
- [ ] **Extract shared `FormatBytes()` utility** — Currently duplicated in 4+ classes
- [ ] **Secure API key storage** — Currently stored as plain text in JSON settings file
- [ ] **Mask API key in logs** — URLs with full API key are written to Playnite log files
- [ ] **Add download resume** — Multi-GB cloud downloads restart from zero on any interruption
- [ ] **Add queue persistence** — Serialize queued/paused downloads to disk to survive restarts

### Future Ideas
- [ ] OneDrive / Dropbox / MEGA cloud providers
- [ ] Direct HTTP/FTP URL support
- [ ] Checksum verification for cloud downloads (SHA256/MD5)
- [ ] Incremental/delta sync (only copy changed files)
- [ ] Batch install/uninstall operations
- [ ] Network bandwidth limiting for cloud downloads
- [ ] Installation size dashboard
- [ ] Settings import/export
- [ ] PS2 (PCSX2) emulator integration
- [ ] Auto-detect 7-Zip installation path
- [ ] Unit test project
- [ ] CI/CD pipeline with GitHub Actions

---

## Known Issues

### Critical
| Issue | Description |
|-------|-------------|
| **`async void ProcessQueue()`** | `CloudDownloadManager.ProcessQueue()` is `async void` — unhandled exceptions crash Playnite. |
| **Parallel download change is no-op** | `SetMaxParallelDownloads()` does nothing; the `SemaphoreSlim` can't be resized after creation. Requires restart. |
| **SemaphoreSlim race condition** | `BackgroundInstallManager.SetMaxParallelInstalls()` disposes the semaphore while active installs may still hold it, causing `ObjectDisposedException`. |

### High
| Issue | Description |
|-------|-------------|
| **Version mismatch** | `extension.yaml` (1.3.5), `PluginVersion` (1.3.4), and `AssemblyInfo.cs` (1.2.1.0) are out of sync. |
| **ArchiveHelper deadlock** | 7-Zip's stdout/stderr are redirected but read only after `WaitForExit()`. Full pipe buffers cause indefinite hang. |
| **Regex JSON parsing** | Google Drive API responses are parsed with regex patterns that break on escaped quotes, Unicode, or field reordering. |
| **Sync-over-async in `GetGames()`** | `Task.Run(...).GetAwaiter().GetResult()` can deadlock if called on a UI synchronization context. |
| **Orphaned temp files** | Crashed cloud downloads leave multi-GB temp files in `%TEMP%` forever. |
| **Emulator profile reflection** | Uses `GetType().GetProperty("SelectableProfiles")` — silently fails if SDK property name changes. |

### Medium
| Issue | Description |
|-------|-------------|
| **Download Manager flicker** | Items are cleared and rebuilt every 1 second, losing scroll position and selection. |
| **No retry on I/O or network error** | A single transient error fails the entire copy or download operation. |
| **Game ID changes on folder rename** | If the source folder is renamed/moved, all game IDs change, losing play time and metadata. |
| **API key stored in plain text** | Google Drive API key is written unencrypted to the Playnite settings JSON file. |
| **Localization file loading fragile** | Searches 6+ hardcoded paths to find XAML localization files. |
| **Duplicate localization include** | `.csproj` includes localization files as both `<None>` and `<EmbeddedResource>`. |

---

## Changelog

See [checklog.txt](checklog.txt) for the historical changelog (v0.1.5 → v1.1.1).

### Latest versions

- **v1.3.5** — Current version
- **v1.3.4** — Fixed localization UI display with comprehensive fallback mechanisms
- **v1.3.3** — Fixed localization with `LocalizedStringExtension` using `ResourceProvider.GetString()`
- **v1.3.2** — Fixed localization display: resources explicitly loaded in settings view
- **v1.3.1** — Fixed installation status preservation and localization resource embedding
- **v1.3.0** — Fixed EmuLibrary conflict: platform/emulator settings preserved during library scans
- **v1.2.0** — Full localization support (English + Italian), auto language selection
- **v1.1.1** — Google Drive integration, cloud download manager, archive extraction
- **v1.0.0** — Background installation, download queue, parallel installs, pause/resume
- **v0.5.x** — Integrity verification, disk space checks, progress tracking
- **v0.4.x** — Emulator integration, platform detection, PS3 support
- **v0.1.x** — Initial release: basic file copy from archive to SSD

---

## Requirements

- Playnite 10.x or later
- .NET Framework 4.8
- (Optional) [7-Zip](https://www.7-zip.org/) for archive extraction (.zip, .rar, .7z)
- (Optional) Google API key for Google Drive integration

---

## License

Distributed under the [MIT](LICENSE) license.

---

## Author

**Sassoanarchico** — [GitHub](https://github.com/sassoanarchico)

---

<div align="center">
<sub>Made with coffee and file copying for Playnite</sub>
</div>
