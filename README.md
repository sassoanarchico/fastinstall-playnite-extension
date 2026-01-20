# FastInstall (Playnite Extension)

FastInstall is a Playnite library extension that helps you **store games on a slow "archive" drive (HDD)** and **install them to a fast drive (SSD)** by copying files when you want to play.

It's designed for large collections (including ROM folders/emulators) where you don't want everything permanently on your SSD.

## Features

### New in 1.2.0
- **Full localization support**:
  - English and Italian translations for all UI, dialogs, and notifications
  - Automatic language selection based on Playnite’s current language
  - Localized error messages for Google Drive, 7-Zip, disk space checks, and integrity verification

### Core Functionality
- **Archive → SSD Installation** - Copy game folders from archive (HDD) to fast drive (SSD)
- **Multiple Folder Configurations** - Configure multiple source/destination path pairs
- **Safe Uninstall** - Remove installed files from SSD without touching the archive
- **Automatic Game Detection** - Scans source directories and detects games automatically

### Installation Management
- **Background Installation** - Non-blocking progress window, keep using Playnite during installation
- **Download Manager** - View and manage all active, queued, and paused installations
- **Installation Queue** - Multiple installations are queued and processed automatically
- **Parallel Downloads** - Configure 1-10 simultaneous installations
- **Pause/Resume** - Pause ongoing installations and resume them later
- **Queue Priority** - Set Low, Normal, or High priority for each installation job
- **Conflict Resolution** - Choose how to handle existing installations (Ask, Overwrite, Skip)

### Progress Tracking
- Real-time progress bar with percentage
- Bytes copied / total size
- Transfer speed (MB/s)
- Elapsed time and ETA
- File count and current file name

### Archive Support
- **Compressed Files** - Automatically extract ZIP, RAR, and 7Z archives before installation
- **7-Zip Integration** - Configure 7-Zip path in settings with download button

### Disk Management
- **Disk Space Check** - Verifies available space before installation
- **Disk Space Preview** - Shows required and available space in notifications
- **Insufficient Space Warning** - Dialog with option to continue anyway

### Integrity & Reliability
- **Integrity Verification** - Verifies all files after copy by comparing file sizes
- **Stable Game Detection** - Consistent GameId generation prevents duplicate entries
- **Path Normalization** - Handles different path formats consistently

### Platform Support
- **Platform Detection** - Automatic detection based on folder structure
- **PS3 Game Detection** - Recognizes PS3_GAME folder structure and EBOOT.BIN files
- **Console/Emulator Support** - Works with various console game structures

### Emulator Integration
- **Emulator Selection** - Configure emulator per folder
- **Profile Selection** - Choose specific emulator profiles
- **Dynamic Profile Filtering** - Shows only profiles for selected emulator
- **Game Launching** - Launch games via configured emulator with proper arguments

### User Experience
- **Playnite Notifications** - Status updates for installation start, complete, and errors
- **Auto-close Progress Window** - Closes automatically after installation completes
- **Italian Notifications** - Completion messages in Italian format

## Installation

1. Download the latest `.pext` from the [GitHub Releases](../../releases) page
2. Double-click the `.pext` file (or install via Playnite → Add-ons → Install from file)
3. Restart Playnite when prompted

## Configuration

Go to: **Playnite → Add-ons → FastInstall → Settings**

### Folder Configuration Table
| Column | Description |
|--------|-------------|
| **Source Path** | Your archived games folder (slow HDD) |
| **Destination Path** | Where games should be copied to (fast SSD) |
| **Platform** | Platform for auto-detection and play actions |
| **Emulator** | (Optional) Emulator configured in Playnite |
| **Profile** | (Optional) Specific emulator profile to use |

### Additional Settings
- **Enable Parallel Downloads** - Allow multiple simultaneous installations
- **Max Parallel Downloads** - Number of concurrent installations (1-10)
- **Conflict Resolution** - How to handle existing installations
- **7-Zip Path** - Path to 7z.exe for archive extraction

## Requirements

- Playnite 10.x or later
- .NET Framework 4.6.2 or later
- (Optional) 7-Zip for archive extraction support

## License

MIT License - See LICENSE file for details.
