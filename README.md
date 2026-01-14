# FastInstall (Playnite Extension)

FastInstall is a Playnite library extension that helps you **store games on a slow “archive” drive (HDD)** and **install them to a fast drive (SSD)** by copying files when you want to play.

It’s designed for large collections (including ROM folders/emulators) where you don’t want everything permanently on your SSD.

## Features

- **Archive → SSD install**: installs by copying a game folder from a source (archive) path to a destination (fast) path.
- **Multiple folder configurations**: configure multiple source/destination pairs.
- **Platform-aware scanning**: supports different folder types (PC and several console/emulator structures).
- **Background install with detailed progress**:
  - progress bar + %
  - bytes copied / total
  - transfer speed
  - elapsed + ETA
  - file count + current file
  - runs in a **non-modal window** so Playnite remains usable during installation
- **Safe uninstall**: removes installed files from the fast drive without touching the archive.

## Installation (recommended)

1. Download the latest `.pext` from the GitHub Releases page.
2. Double-click the `.pext` (or install via Playnite → Add-ons → Install from file).
3. Restart Playnite when prompted.

## Configuration

Go to:

Playnite → Add-ons → **FastInstall** → **Settings**

Add one or more configurations:

- **Source Path (Archive HDD)**: your archived games folder (slow drive).
- **Destination Path (Fast SSD)**: where games should be copied to (fast drive).
- **Platform**: used for auto-detection and play actions.
- **Emulator (Optional)**: pick an emulator configured in Playnite.

## License

Add a license if you plan to distribute publicly.

