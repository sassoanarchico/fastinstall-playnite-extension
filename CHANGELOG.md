# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [1.3.5] - 2026-02-25

### Fixed
- Current stable release

## [1.3.4] - 2026-02-24

### Fixed
- Localization UI display with comprehensive fallback mechanisms

## [1.3.3] - 2026-02-23

### Fixed
- Localization with `LocalizedStringExtension` using `ResourceProvider.GetString()`

## [1.3.2] - 2026-02-22

### Fixed
- Localization display: resources explicitly loaded in settings view

## [1.3.1] - 2026-02-21

### Fixed
- Installation status preservation during library scans
- Localization resource embedding in output

## [1.3.0] - 2026-02-20

### Fixed
- EmuLibrary conflict: platform and emulator settings are now preserved during library scans

## [1.2.0] - 2026-02-18

### Added
- **Full localization support**: English (en_US) and Italian (it_IT) with ~200+ localization keys
- Automatic language selection based on Playnite language setting
- `LocalizedStringExtension` XAML markup extension with 3-level fallback
- `LocalizationConverters` for localized enum display in UI

## [1.1.1] - 2025-08-10

### Changed
- Cloud games now show "[Cloud]" suffix at the end of the title instead of prefix
- Removed Dropbox support — extension now focuses exclusively on Google Drive integration

### Improved
- Google Drive download handling — uses API v3 when API key is configured to bypass virus scan warnings
- Enhanced virus scan warning detection and token extraction with 8 different pattern matching methods
- Better error messages for Google Drive download failures with specific HTTP status code handling

### Fixed
- File ID extraction from GameId now uses pipe separator (`|`) instead of underscore to handle Google Drive IDs containing underscores
- Improved download URL fallback mechanism when API v3 fails

## [1.1.0] - 2025-08-05

### Added
- **Google Drive Integration** — download games directly from Google Drive cloud storage
- Cloud Sources section in settings — configure Google Drive links (direct files or shared folders)
- Google Drive API Key configuration
- Cloud download manager with progress tracking — download, pause, resume cloud transfers
- Automatic archive detection for cloud downloads — ZIP/RAR/7Z files are extracted automatically
- Cloud game identification with "[Cloud]" prefix in library
- `CloudInstallController` — dedicated install controller for cloud games
- Test Connection button for cloud sources
- Unified download queue for both local and cloud installations

## [1.0.0] - 2025-07-28

### Added
- **Conflict resolution settings** — choose how to handle existing installations (Ask, Overwrite, Skip)
- **Priority system** for installation queue — Low, Normal, High priority
- **Archive support** — automatically extracts ZIP, RAR, 7Z archives before installation using 7-Zip
- 7-Zip configuration in settings with download button
- Disk space preview — shows required and available space before starting installation

### Fixed
- Stable GameId generation using custom hash algorithm instead of `GetHashCode()`
- Path normalization for consistent game matching across scans

## [0.5.3] - 2025-07-20

### Fixed
- GameId generation now uses stable hash algorithm
- Path normalization ensures consistent GameId generation
- Duplicate game entries no longer appear when re-scanning library

## [0.5.1] - 2025-07-15

### Fixed
- Pause button now correctly pauses installations instead of cancelling them
- Partial files are preserved on pause — installation can be resumed
- Pause/Resume button state now updates correctly in the progress window
- `BackgroundInstallManager.Instance` now returns null instead of throwing exception

### Added
- Enable Parallel Downloads checkbox in settings
- Max Parallel Downloads numeric field (1–10)

## [0.5.0] - 2025-07-10

### Added
- **Complete Download Manager** with full queue management
- `DownloadManagerWindow` — shows all active, queued, and paused installations
- **Parallel installation support** — configure 1–10 simultaneous downloads
- **Pause/Resume** functionality for installations
- "Open Download Manager" button in settings

## [0.4.3] - 2025-07-05

### Fixed
- Cancellation of queued jobs now works correctly
- Queued jobs that are cancelled no longer show "Cleaning up" message
- Games can be re-installed immediately after cancelling queued installations

## [0.4.2] - 2025-06-28

### Fixed
- GameId generation now ignores PS3-style codes in brackets (e.g. `[BCES01141]`)

### Added
- Emulator profile selection in separate column
- Profile dropdown filters to show only profiles for the selected emulator

## [0.4.1] - 2025-06-25

### Fixed
- Cancelled installations now release the "active installation" state immediately
- Progress window on cancellation no longer gets stuck
- Background cleanup after cancellation runs independently from the UI

## [0.4.0] - 2025-06-20

### Added
- **Emulator profile selection** — select specific emulator profiles from dropdown
- `{ImagePath}` placeholder support in profile arguments

### Improved
- `LoadAvailableEmulators` now loads all profiles for each emulator

## [0.3.0] - 2025-06-10

### Added
- **Integrity check after copy** — verifies all files were copied correctly by comparing file sizes
- `IntegrityCheckResult` class for verification reports

## [0.2.0] - 2025-05-25

### Added
- **Disk space check** before installation — verifies available space on destination drive
- Warning dialog if insufficient disk space is detected

## [0.1.9] - 2025-05-20

### Added
- Progress window now closes automatically 2 seconds after installation completes
- `SanitizeFileName` helper function for invalid Windows path characters

## [0.1.8] - 2025-05-15

### Fixed
- Games with renamed titles (especially PS3 games with codes) are now correctly found using GameId matching and fuzzy name matching

## [0.1.7] - 2025-05-10

### Fixed
- Games with renamed titles are now correctly found by searching source folders for game files
- Cleanup of cancelled installations now runs asynchronously

## [0.1.6] - 2025-05-05

### Added
- **Background install queue** — multiple installations are queued and processed sequentially

### Fixed
- Cancelled jobs are correctly skipped if cancelled before starting

## [0.1.5] - 2025-04-28

### Added
- **Initial Release** — FastInstall Library Plugin
- Settings UI with configurable folder table (Source Path, Destination Path, Platform, Emulator)
- Automatic game detection from source directories
- PS3 game detection (PS3_GAME folder structure, EBOOT.BIN)
- Installation progress window with real-time progress bar
- File copy with progress tracking (speed, files, percentage)
- Install/Uninstall controllers
- Emulator integration for launching games
- Playnite notifications for installation status
