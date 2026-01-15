using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.Threading.Tasks;

namespace FastInstall
{
    /// <summary>
    /// Detected game type based on folder structure
    /// </summary>
    public enum GameType
    {
        Unknown,
        PS3,        // PlayStation 3 (RPCS3)
        PS2,        // PlayStation 2 (PCSX2)
        PSP,        // PlayStation Portable (PPSSPP)
        Switch,     // Nintendo Switch (Ryujinx/Yuzu)
        WiiU,       // Nintendo Wii U (Cemu)
        Wii,        // Nintendo Wii (Dolphin)
        GameCube,   // Nintendo GameCube (Dolphin)
        Xbox360,    // Xbox 360 (Xenia)
        PC          // PC Game
    }

    /// <summary>
    /// Information about a detected game
    /// </summary>
    public class DetectedGameInfo
    {
        public string Name { get; set; }
        public string FolderPath { get; set; }
        public GameType GameType { get; set; }
        public string ExecutablePath { get; set; }  // Path to main game file (EBOOT.BIN, .xci, etc.)
        public string PlatformName { get; set; }
    }

    public class FastInstallPlugin : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private FastInstallSettingsViewModel settingsViewModel;

        public const string PluginVersion = "1.0.0";
        
        public override Guid Id { get; } = Guid.Parse("F8A1B2C3-D4E5-6789-ABCD-EF1234567890");
        public override string Name => "FastInstall";

        public FastInstallSettings Settings => settingsViewModel?.Settings;

        private bool cleanupDone = false;
        
        public FastInstallPlugin(IPlayniteAPI api) : base(api)
        {
            settingsViewModel = new FastInstallSettingsViewModel(this);
            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };
            
            // Initialize the background install manager with 7-Zip path getter
            BackgroundInstallManager.Initialize(api, () => settingsViewModel.Settings?.SevenZipPath ?? string.Empty);
            
            // Apply max parallel downloads setting
            BackgroundInstallManager.Instance?.SetMaxParallelInstalls(settingsViewModel.Settings.EffectiveMaxParallelDownloads);
        }
        
        /// <summary>
        /// Removes problematic GameActions from games that have {ImagePath} or empty paths
        /// This is called lazily when needed
        /// </summary>
        private void CleanupProblematicGameActions()
        {
            if (cleanupDone) return;
            cleanupDone = true;
            
            try
            {
                var ourGames = PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList();
                logger.Info($"FastInstall: Checking {ourGames.Count} games for problematic GameActions");
                
                int fixedCount = 0;
                
                foreach (var game in ourGames)
                {
                    if (game.GameActions == null || game.GameActions.Count == 0)
                        continue;
                    
                    // Find and remove GameActions with Type = Emulator that might cause {ImagePath} issues
                    var problematicActions = game.GameActions
                        .Where(a => a.Type == GameActionType.Emulator && a.IsPlayAction)
                        .ToList();
                    
                    if (problematicActions.Count > 0)
                    {
                        logger.Info($"FastInstall: Removing {problematicActions.Count} emulator GameActions from '{game.Name}'");
                        
                        foreach (var action in problematicActions)
                        {
                            logger.Debug($"FastInstall: Removing action '{action.Name}' (EmulatorId: {action.EmulatorId}, Path: {action.Path ?? "(null)"})");
                            game.GameActions.Remove(action);
                        }
                        
                        PlayniteApi.Database.Games.Update(game);
                        fixedCount++;
                    }
                }
                
                if (fixedCount > 0)
                {
                    logger.Info($"FastInstall: Fixed {fixedCount} games with problematic GameActions");
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "FastInstall_Cleanup",
                        $"FastInstall: Fixed {fixedCount} games with outdated emulator configurations.",
                        NotificationType.Info));
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FastInstall: Error cleaning up problematic GameActions");
            }
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            // Clean up any problematic GameActions from previously imported games
            CleanupProblematicGameActions();
            
            var games = new List<GameMetadata>();

            // Check if we have any enabled configurations
            var enabledConfigs = Settings?.FolderConfigurations?.Where(c => c.IsEnabled).ToList();
            
            if (enabledConfigs == null || enabledConfigs.Count == 0)
            {
                logger.Warn("FastInstall: No enabled folder configurations found.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "FastInstall_NoConfig",
                    "FastInstall: Please configure at least one enabled folder pair in settings.",
                    NotificationType.Error,
                    () => PlayniteApi.MainView.OpenPluginSettings(Id)));
                return games;
            }

            // Scan each enabled configuration
            foreach (var config in enabledConfigs)
            {
                if (string.IsNullOrWhiteSpace(config.SourcePath))
                {
                    logger.Warn($"FastInstall: Skipping configuration with empty source path.");
                    continue;
                }

                if (!Directory.Exists(config.SourcePath))
                {
                    logger.Error($"FastInstall: Source directory does not exist: {config.SourcePath}");
                    continue;
                }

                try
                {
                    var directories = Directory.GetDirectories(config.SourcePath);
                    logger.Info($"FastInstall: Found {directories.Length} game folders in '{config.SourcePath}'.");

                    // Normalize source path once for all games in this configuration
                    var normalizedConfigSourcePath = Path.GetFullPath(config.SourcePath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .ToLowerInvariant();
                    
                    foreach (var dir in directories)
                    {
                        var detectedGame = DetectGameInfo(dir);
                        var gameId = GenerateGameId(normalizedConfigSourcePath, detectedGame.Name);

                        // Check if game is already installed on SSD
                        bool isInstalled = false;
                        string installDir = null;

                        if (!string.IsNullOrWhiteSpace(config.DestinationPath))
                        {
                            var potentialInstallPath = Path.Combine(config.DestinationPath, detectedGame.Name);
                            if (Directory.Exists(potentialInstallPath))
                            {
                                isInstalled = true;
                                installDir = potentialInstallPath;
                            }
                        }

                        // Override platform if specified in configuration
                        var platformName = detectedGame.PlatformName;
                        if (!string.IsNullOrWhiteSpace(config.Platform) && config.Platform != "PC")
                        {
                            platformName = config.Platform;
                        }

                        var game = new GameMetadata
                        {
                            GameId = gameId,
                            Name = detectedGame.Name,
                            IsInstalled = isInstalled,
                            InstallDirectory = installDir,
                            Source = new MetadataNameProperty("FastInstall Archive"),
                            Platforms = new HashSet<MetadataProperty> { new MetadataNameProperty(platformName) }
                            // Note: We don't add GameActions here - FastInstallPlayController handles game launching
                            // This avoids issues with Playnite's emulator {ImagePath} placeholder not being resolved correctly
                        };

                        games.Add(game);
                        logger.Debug($"FastInstall: Added game '{detectedGame.Name}' (Platform: {platformName}, Installed: {isInstalled})");
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"FastInstall: Error scanning directory '{config.SourcePath}'.");
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        $"FastInstall_ScanError_{config.SourcePath.GetHashCode()}",
                        $"FastInstall: Error scanning '{config.SourcePath}': {ex.Message}",
                        NotificationType.Error));
                }
            }

            return games;
        }

        /// <summary>
        /// Detects the game type and information from a folder
        /// </summary>
        public DetectedGameInfo DetectGameInfo(string folderPath)
        {
            var gameName = Path.GetFileName(folderPath);
            var info = new DetectedGameInfo
            {
                Name = gameName,
                FolderPath = folderPath,
                GameType = GameType.Unknown,
                PlatformName = "PC (Windows)"
            };

            // Check for PS3 game structure
            var ps3GameFolder = Path.Combine(folderPath, "PS3_GAME");
            var ps3EbootBin = Path.Combine(folderPath, "PS3_GAME", "USRDIR", "EBOOT.BIN");
            var ps3ParamSfo = Path.Combine(folderPath, "PS3_GAME", "PARAM.SFO");
            
            // Also check for disc structure (where EBOOT.BIN is at root level for some dumps)
            var rootEbootBin = Path.Combine(folderPath, "USRDIR", "EBOOT.BIN");
            
            if (Directory.Exists(ps3GameFolder) || File.Exists(ps3ParamSfo))
            {
                info.GameType = GameType.PS3;
                info.PlatformName = "Sony PlayStation 3";
                
                // Find EBOOT.BIN
                if (File.Exists(ps3EbootBin))
                {
                    info.ExecutablePath = ps3EbootBin;
                }
                else if (File.Exists(rootEbootBin))
                {
                    info.ExecutablePath = rootEbootBin;
                }
                else
                {
                    // Search for EBOOT.BIN recursively
                    var ebootFiles = Directory.GetFiles(folderPath, "EBOOT.BIN", SearchOption.AllDirectories);
                    if (ebootFiles.Length > 0)
                    {
                        info.ExecutablePath = ebootFiles[0];
                    }
                }

                // Try to get actual game name from PARAM.SFO (future enhancement)
                // For now, use folder name
                logger.Debug($"FastInstall: Detected PS3 game '{gameName}' with EBOOT: {info.ExecutablePath}");
                return info;
            }

            // Check for Nintendo Switch game (.xci, .nsp, .nsz files)
            var switchFiles = GetFilesWithExtensions(folderPath, new[] { ".xci", ".nsp", ".nsz" });
            if (switchFiles.Count > 0)
            {
                info.GameType = GameType.Switch;
                info.PlatformName = "Nintendo Switch";
                info.ExecutablePath = switchFiles[0];
                logger.Debug($"FastInstall: Detected Switch game '{gameName}'");
                return info;
            }

            // Check for Wii U game
            var wiiuRpx = GetFilesWithExtensions(folderPath, new[] { ".rpx", ".wud", ".wux" });
            if (wiiuRpx.Count > 0)
            {
                info.GameType = GameType.WiiU;
                info.PlatformName = "Nintendo Wii U";
                info.ExecutablePath = wiiuRpx[0];
                return info;
            }

            // Check for Wii/GameCube game
            var wiiFiles = GetFilesWithExtensions(folderPath, new[] { ".wbfs", ".iso", ".gcm", ".ciso" });
            if (wiiFiles.Count > 0)
            {
                // Try to determine if it's Wii or GameCube
                var fileName = Path.GetFileName(wiiFiles[0]).ToLowerInvariant();
                if (fileName.Contains("gc") || fileName.EndsWith(".gcm"))
                {
                    info.GameType = GameType.GameCube;
                    info.PlatformName = "Nintendo GameCube";
                }
                else
                {
                    info.GameType = GameType.Wii;
                    info.PlatformName = "Nintendo Wii";
                }
                info.ExecutablePath = wiiFiles[0];
                return info;
            }

            // Check for PSP game
            var pspIso = GetFilesWithExtensions(folderPath, new[] { ".iso", ".cso" });
            var pspEboot = Path.Combine(folderPath, "PSP_GAME", "SYSDIR", "EBOOT.BIN");
            if (File.Exists(pspEboot))
            {
                info.GameType = GameType.PSP;
                info.PlatformName = "Sony PSP";
                info.ExecutablePath = pspEboot;
                return info;
            }

            // Check for Xbox 360 game (.xex, .iso files)
            var xbox360Files = GetFilesWithExtensions(folderPath, new[] { ".xex" });
            var defaultXex = Path.Combine(folderPath, "default.xex");
            if (File.Exists(defaultXex) || xbox360Files.Count > 0)
            {
                info.GameType = GameType.Xbox360;
                info.PlatformName = "Microsoft Xbox 360";
                info.ExecutablePath = File.Exists(defaultXex) ? defaultXex : xbox360Files[0];
                return info;
            }

            // Check for PC game (.exe files)
            var exeFiles = GetFilesWithExtensions(folderPath, new[] { ".exe" });
            if (exeFiles.Count > 0)
            {
                info.GameType = GameType.PC;
                info.PlatformName = "PC (Windows)";
                // Try to find the most likely game executable
                info.ExecutablePath = FindBestExecutable(exeFiles, gameName);
                return info;
            }

            return info;
        }

        /// <summary>
        /// Gets files with specified extensions from a folder (searches recursively)
        /// </summary>
        private List<string> GetFilesWithExtensions(string folderPath, string[] extensions)
        {
            var files = new List<string>();
            try
            {
                foreach (var ext in extensions)
                {
                    files.AddRange(Directory.GetFiles(folderPath, $"*{ext}", SearchOption.AllDirectories));
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"FastInstall: Error searching for files in {folderPath}");
            }
            return files;
        }

        /// <summary>
        /// Tries to find the best executable from a list
        /// </summary>
        private string FindBestExecutable(List<string> exeFiles, string gameName)
        {
            if (exeFiles.Count == 0) return null;
            if (exeFiles.Count == 1) return exeFiles[0];

            // Prefer executables that match the game name
            var gameNameLower = gameName.ToLowerInvariant();
            foreach (var exe in exeFiles)
            {
                var exeName = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (gameNameLower.Contains(exeName) || exeName.Contains(gameNameLower))
                {
                    return exe;
                }
            }

            // Avoid common non-game executables
            var avoid = new[] { "unins", "setup", "config", "launcher", "crash", "report", "update", "redist" };
            foreach (var exe in exeFiles)
            {
                var exeName = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (!avoid.Any(a => exeName.Contains(a)))
                {
                    return exe;
                }
            }

            return exeFiles[0];
        }

        /// <summary>
        /// Gets the detected game info for an installed game
        /// </summary>
        public DetectedGameInfo GetDetectedGameInfo(Game game)
        {
            string basePath = null;

            if (game.IsInstalled && !string.IsNullOrWhiteSpace(game.InstallDirectory))
            {
                basePath = game.InstallDirectory;
            }
            else
            {
                basePath = GetSourcePath(game);
            }

            // If still null, try GameActions path
            if (string.IsNullOrWhiteSpace(basePath) && game.GameActions?.Any() == true)
            {
                var actPath = game.GameActions.FirstOrDefault(a => a.IsPlayAction)?.Path;
                if (!string.IsNullOrWhiteSpace(actPath))
                {
                    basePath = File.Exists(actPath) ? Path.GetDirectoryName(actPath) : actPath;
                }
            }

            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            {
                return null;
            }

            return DetectGameInfo(basePath);
        }

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return null;
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new FastInstallController(args.Game, this);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new FastUninstallController(args.Game, this);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            logger.Info($"FastInstall: GetPlayActions called for '{args.Game.Name}' (PluginId: {args.Game.PluginId})");
            logger.Debug($"FastInstall: Expected PluginId: {Id}");
            logger.Debug($"FastInstall: IsInstalled: {args.Game.IsInstalled}");
            logger.Debug($"FastInstall: InstallDirectory: {args.Game.InstallDirectory ?? "(null)"}");
            
            if (args.Game.PluginId != Id)
            {
                logger.Debug($"FastInstall: Game not managed by this plugin, skipping");
                yield break;
            }

            // Try to get game info - check multiple locations
            var gameInfo = GetDetectedGameInfo(args.Game);
            
            // If game info is null but game has a valid destination path, try to detect from there
            if (gameInfo == null)
            {
                var destPath = GetDestinationPath(args.Game);
                logger.Debug($"FastInstall: gameInfo is null, trying destination path: {destPath ?? "(null)"}");
                
                if (!string.IsNullOrWhiteSpace(destPath) && Directory.Exists(destPath))
                {
                    gameInfo = DetectGameInfo(destPath);
                    logger.Debug($"FastInstall: Detected game type from destination: {gameInfo?.GameType}");
                }
            }
            
            // Also try source path if still null
            if (gameInfo == null)
            {
                var sourcePath = GetSourcePath(args.Game);
                logger.Debug($"FastInstall: gameInfo still null, trying source path: {sourcePath ?? "(null)"}");
                
                if (!string.IsNullOrWhiteSpace(sourcePath) && Directory.Exists(sourcePath))
                {
                    gameInfo = DetectGameInfo(sourcePath);
                    logger.Debug($"FastInstall: Detected game type from source: {gameInfo?.GameType}");
                }
            }
            
            // Get the configuration to determine platform
            var config = GetGameConfiguration(args.Game);
            var configPlatform = config?.Platform;
            logger.Debug($"FastInstall: Configuration platform: {configPlatform ?? "(null)"}");
            
            // If gameInfo is null or Unknown, try to determine from configuration
            if (gameInfo == null || gameInfo.GameType == GameType.Unknown)
            {
                var anyPath = args.Game.InstallDirectory ?? GetDestinationPath(args.Game) ?? GetSourcePath(args.Game);
                
                if (string.IsNullOrWhiteSpace(anyPath))
                {
                    logger.Error($"FastInstall: No valid path found for game '{args.Game.Name}', cannot provide play action");
                    yield break;
                }
                
                // Determine game type from configuration platform
                var gameType = GameType.Unknown;
                var platformName = configPlatform ?? "Unknown";
                
                if (!string.IsNullOrWhiteSpace(configPlatform))
                {
                    if (configPlatform.IndexOf("PlayStation 3", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.PS3;
                        platformName = "Sony PlayStation 3";
                    }
                    else if (configPlatform.IndexOf("PlayStation 2", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.PS2;
                        platformName = "Sony PlayStation 2";
                    }
                    else if (configPlatform.IndexOf("PSP", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.PSP;
                        platformName = "Sony PSP";
                    }
                    else if (configPlatform.IndexOf("Switch", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.Switch;
                        platformName = "Nintendo Switch";
                    }
                    else if (configPlatform.IndexOf("Wii U", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.WiiU;
                        platformName = "Nintendo Wii U";
                    }
                    else if (configPlatform.IndexOf("Wii", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.Wii;
                        platformName = "Nintendo Wii";
                    }
                    else if (configPlatform.IndexOf("GameCube", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.GameCube;
                        platformName = "Nintendo GameCube";
                    }
                    else if (configPlatform.IndexOf("Xbox 360", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.Xbox360;
                        platformName = "Microsoft Xbox 360";
                    }
                    else if (configPlatform.IndexOf("PC", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        gameType = GameType.PC;
                        platformName = "PC (Windows)";
                    }
                }
                
                logger.Info($"FastInstall: Using platform from config: {platformName} (GameType: {gameType})");
                
                gameInfo = new DetectedGameInfo
                {
                    Name = args.Game.Name,
                    FolderPath = anyPath,
                    GameType = gameType,
                    PlatformName = platformName
                };
            }
            
            // Override game type from config if current detection is Unknown
            if (gameInfo.GameType == GameType.Unknown && !string.IsNullOrWhiteSpace(configPlatform))
            {
                if (configPlatform.IndexOf("PlayStation 3", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    gameInfo.GameType = GameType.PS3;
                    gameInfo.PlatformName = "Sony PlayStation 3";
                    logger.Info($"FastInstall: Overriding game type to PS3 from configuration");
                }
            }

            logger.Info($"FastInstall: Returning FastInstallPlayController for '{args.Game.Name}' (Type: {gameInfo.GameType})");
            yield return new FastInstallPlayController(args.Game, this, gameInfo);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new FastInstallSettingsView();
        }

        /// <summary>
        /// Generates a consistent game ID from the source path and game name.
        /// IMPORTANT: ignores PS3-style codes in brackets (e.g. "[BCES01141]") so that
        /// renaming folders from "Game [BCES01141]" to "Game" does NOT change the GameId.
        /// Also normalizes spaces and case to keep IDs stable.
        /// </summary>
        public string GenerateGameId(string sourcePath, string gameName)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentNullException(nameof(sourcePath));
            }

            if (string.IsNullOrWhiteSpace(gameName))
            {
                gameName = string.Empty;
            }

            // Normalize path to ensure consistent hashing (handle trailing slashes, case, etc.)
            // Note: sourcePath should already be normalized when passed, but we normalize again for safety
            string normalizedPath;
            if (Path.IsPathRooted(sourcePath))
            {
                normalizedPath = Path.GetFullPath(sourcePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant(); // Normalize case for consistent hashing
            }
            else
            {
                normalizedPath = sourcePath
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant();
            }
            
            // Use stable hash algorithm instead of GetHashCode() which can vary between runs
            var sourceHash = GetStableHash(normalizedPath).ToString("X8");

            // Normalize game name:
            // - lower case
            // - remove codes in square brackets (e.g. " [BCES01141]")
            // - collapse multiple spaces
            // - replace spaces with underscore for ID
            var name = gameName.ToLowerInvariant().Trim();

            // Remove any "[...]" segments (common for PS3 dumps with codes)
            name = Regex.Replace(name, @"\s*\[[^\]]+\]\s*", " ");

            // Collapse multiple spaces
            name = Regex.Replace(name, @"\s+", " ").Trim();

            var cleanName = name.Replace(" ", "_");

            return $"fastinstall_{sourceHash}_{cleanName}";
        }

        /// <summary>
        /// Generates a stable hash code for a string that remains consistent across application runs
        /// Uses a simple hash algorithm that produces the same result for the same input
        /// </summary>
        private int GetStableHash(string input)
        {
            if (string.IsNullOrEmpty(input))
                return 0;

            unchecked
            {
                int hash = 17;
                foreach (char c in input)
                {
                    hash = hash * 31 + char.ToLowerInvariant(c);
                }
                return hash;
            }
        }

        /// <summary>
        /// Sanitizes a file/folder name by removing invalid characters for Windows paths
        /// Replaces invalid characters with safe alternatives
        /// </summary>
        public string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }

            // Characters invalid in Windows file/folder names: < > : " | ? * \
            // Also remove control characters
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = fileName;

            // Replace invalid characters with safe alternatives
            foreach (var invalidChar in invalidChars)
            {
                sanitized = sanitized.Replace(invalidChar, '_');
            }

            // Also handle common problematic characters
            sanitized = sanitized.Replace(':', '-'); // Replace colon with dash (common in game titles)
            sanitized = sanitized.Trim(); // Remove leading/trailing spaces
            sanitized = sanitized.TrimEnd('.'); // Remove trailing dots (not allowed in Windows)

            // Remove any remaining control characters
            sanitized = new string(sanitized.Where(c => !char.IsControl(c)).ToArray());

            return sanitized;
        }

        /// <summary>
        /// Gets the configuration that contains this game
        /// </summary>
        private FolderConfiguration GetGameConfiguration(Game game)
        {
            if (Settings?.FolderConfigurations == null)
                return null;

            foreach (var config in Settings.FolderConfigurations.Where(c => c.IsEnabled))
            {
                var src = config.SourcePath;
                var dst = config.DestinationPath;

                // Match by install directory (preferred)
                if (!string.IsNullOrWhiteSpace(dst) && !string.IsNullOrWhiteSpace(game.InstallDirectory))
                {
                    var installFull = Path.GetFullPath(game.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
                    var dstFull = Path.GetFullPath(dst).TrimEnd(Path.DirectorySeparatorChar);
                    if (installFull.StartsWith(dstFull, StringComparison.OrdinalIgnoreCase))
                    {
                        return config;
                    }
                }

                // Match by archive directory
                if (!string.IsNullOrWhiteSpace(src))
                {
                    var archivePath = Path.Combine(src, game.Name);
                    if (Directory.Exists(archivePath))
                    {
                        return config;
                    }

                    // If renamed in Playnite, try to find folder by matching GameId (more efficient: just compare GameIds)
                    try
                    {
                        var dirs = Directory.GetDirectories(src);
                        var normalizedSrc = Path.GetFullPath(src)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .ToLowerInvariant(); // Normalize case for consistent hashing
                        
                        foreach (var dir in dirs)
                        {
                            var folderName = Path.GetFileName(dir);
                            var potentialGameId = GenerateGameId(normalizedSrc, folderName);
                            if (potentialGameId == game.GameId)
                            {
                                logger.Debug($"FastInstall: Found config match for '{game.Name}' via GameId match with folder '{folderName}'");
                                return config;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, $"FastInstall: Error searching archive folders for config match for '{game.Name}'");
                    }
                }

                // Match by destination directory even if not installed flag
                if (!string.IsNullOrWhiteSpace(dst))
                {
                    var destPath = Path.Combine(dst, game.Name);
                    if (Directory.Exists(destPath))
                    {
                        return config;
                    }

                    // If renamed in Playnite, try to find by matching GameId in destination
                    try
                    {
                        var dirs = Directory.GetDirectories(dst);
                        // Use source path for GameId generation if available (GameId is based on source path), otherwise destination
                        var basePath = !string.IsNullOrWhiteSpace(src) ? src : dst;
                        var normalizedBasePath = Path.GetFullPath(basePath)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .ToLowerInvariant(); // Normalize case for consistent hashing
                        
                        foreach (var dir in dirs)
                        {
                            var folderName = Path.GetFileName(dir);
                            var potentialGameId = GenerateGameId(normalizedBasePath, folderName);
                            if (potentialGameId == game.GameId)
                            {
                                logger.Debug($"FastInstall: Found config match for '{game.Name}' via GameId match in destination folder '{folderName}'");
                                return config;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, $"FastInstall: Error searching destination folders for config match for '{game.Name}'");
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the source path for a game from the archive directory
        /// Handles cases where game.Name was changed in Playnite but folder name is different (e.g., PS3 games with codes like [BLUS12345])
        /// </summary>
        public string GetSourcePath(Game game)
        {
            var config = GetGameConfiguration(game);
            if (config == null || string.IsNullOrWhiteSpace(config.SourcePath))
            {
                logger.Warn($"FastInstall: No configuration found for game '{game.Name}' (GameId: {game.GameId})");
                return null;
            }

            // First try: use game.Name directly (works if name wasn't changed)
            var directPath = Path.Combine(config.SourcePath, game.Name);
            if (Directory.Exists(directPath))
            {
                logger.Debug($"FastInstall: Found source path using direct name match: '{directPath}'");
                return directPath;
            }

            // Second try: search for folder by matching GameId (handles renamed games, PS3 codes, etc.)
            logger.Debug($"FastInstall: Direct path not found for '{game.Name}', searching by GameId in '{config.SourcePath}'...");
            logger.Debug($"FastInstall: Looking for GameId: '{game.GameId}'");
            
            try
            {
                var directories = Directory.GetDirectories(config.SourcePath);
                logger.Debug($"FastInstall: Scanning {directories.Length} folders to find match for GameId '{game.GameId}'");
                
                // Normalize source path for consistent GameId generation
                var normalizedSourcePath = Path.GetFullPath(config.SourcePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant(); // Normalize case for consistent hashing
                
                foreach (var dir in directories)
                {
                    var folderName = Path.GetFileName(dir);
                    
                    // Generate GameId for this folder and compare with the game's GameId
                    var potentialGameId = GenerateGameId(normalizedSourcePath, folderName);
                    
                    if (potentialGameId == game.GameId)
                    {
                        logger.Info($"FastInstall: Found source folder for '{game.Name}' (folder name: '{folderName}') via GameId match");
                        return dir;
                    }
                }
                
                // Log all potential GameIds for debugging
                logger.Debug($"FastInstall: Scanned folders, no GameId match found. Sample GameIds from folders:");
                foreach (var dir in directories.Take(5)) // Log first 5 for debugging
                {
                    var folderName = Path.GetFileName(dir);
                    var sampleGameId = GenerateGameId(normalizedSourcePath, folderName);
                    logger.Debug($"FastInstall:   Folder '{folderName}' -> GameId '{sampleGameId}'");
                }
                
                logger.Warn($"FastInstall: No folder found matching GameId '{game.GameId}' for game '{game.Name}' in '{config.SourcePath}'");
                
                // Fallback: try fuzzy name matching (ignore codes in brackets like [BLUS12345])
                logger.Debug($"FastInstall: Trying fuzzy name match for '{game.Name}'...");
                var gameNameClean = game.Name.ToLowerInvariant().Trim();
                
                foreach (var dir in directories)
                {
                    var folderName = Path.GetFileName(dir);
                    var folderNameClean = folderName.ToLowerInvariant().Trim();
                    
                    // Remove codes in brackets for comparison (e.g., "Game [BLUS12345]" -> "Game")
                    var folderNameWithoutCode = Regex.Replace(folderNameClean, @"\s*\[.*?\]\s*", "").Trim();
                    var gameNameWithoutCode = Regex.Replace(gameNameClean, @"\s*\[.*?\]\s*", "").Trim();
                    
                    // Check if names match (with or without codes)
                    if (folderNameWithoutCode == gameNameWithoutCode || 
                        folderNameClean == gameNameClean ||
                        folderNameClean.StartsWith(gameNameClean) || 
                        gameNameClean.StartsWith(folderNameWithoutCode))
                    {
                        // Verify it's a game folder
                        var detected = DetectGameInfo(dir);
                        if (detected != null)
                        {
                            logger.Info($"FastInstall: Found source folder for '{game.Name}' via fuzzy name match: '{folderName}'");
                            return dir;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"FastInstall: Error searching for source folder for '{game.Name}' in '{config.SourcePath}'");
            }

            // Final fallback: return the direct path anyway (will fail later with a clear error)
            logger.Warn($"FastInstall: Could not find source folder for '{game.Name}' in '{config.SourcePath}'. Returning direct path (will likely fail).");
            return directPath;
        }

        /// <summary>
        /// Gets the destination path for a game in the fast install directory
        /// Sanitizes the game name to ensure valid Windows path
        /// </summary>
        public string GetDestinationPath(Game game)
        {
            var config = GetGameConfiguration(game);
            if (config == null || string.IsNullOrWhiteSpace(config.DestinationPath))
            {
                return null;
            }

            // Sanitize game name to avoid invalid characters in path (e.g., ":" in "Ratchet and Clank: All 4 One")
            var sanitizedName = SanitizeFileName(game.Name);
            return Path.Combine(config.DestinationPath, sanitizedName);
        }

        /// <summary>
        /// Gets the emulator path for a game if configured
        /// </summary>
        public string GetEmulatorPath(Game game)
        {
            var config = GetGameConfiguration(game);
            return config?.EmulatorPath;
        }

        /// <summary>
        /// Gets the emulator configuration for a game
        /// </summary>
        public Emulator GetEmulatorForGame(Game game)
        {
            var config = GetGameConfiguration(game);
            if (config == null || !config.EmulatorId.HasValue || config.EmulatorId.Value == Guid.Empty)
            {
                return null;
            }

            try
            {
                var emulator = PlayniteApi.Database.Emulators.Get(config.EmulatorId.Value);
                if (emulator == null)
                {
                    logger.Warn($"FastInstall: Emulator with ID {config.EmulatorId} not found");
                }
                return emulator;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"FastInstall: Error getting emulator for game '{game.Name}'");
                return null;
            }
        }

        /// <summary>
        /// Gets the emulator profile for a game if configured
        /// </summary>
        public EmulatorProfile GetEmulatorProfileForGame(Game game)
        {
            var config = GetGameConfiguration(game);
            if (config == null || !config.EmulatorId.HasValue || config.EmulatorId.Value == Guid.Empty)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(config.EmulatorProfileId))
            {
                return null;
            }

            try
            {
                var emulator = GetEmulatorForGame(game);
                if (emulator == null)
                {
                    return null;
                }

                // Use reflection to safely access SelectableProfiles/Profiles property
                var profilesProperty = emulator.GetType().GetProperty("SelectableProfiles")
                                     ?? emulator.GetType().GetProperty("Profiles");
                if (profilesProperty == null)
                {
                    return null;
                }

                var profiles = profilesProperty.GetValue(emulator) as IEnumerable;
                if (profiles == null)
                {
                    return null;
                }

                foreach (var profileObj in profiles)
                {
                    var profile = profileObj as EmulatorProfile;
                    if (profile != null)
                    {
                        var idProperty = profile.GetType().GetProperty("Id");
                        var profileId = idProperty?.GetValue(profile)?.ToString();
                        
                        if (profileId == config.EmulatorProfileId)
                        {
                            return profile;
                        }
                    }
                }

                logger.Warn($"FastInstall: Emulator profile with ID '{config.EmulatorProfileId}' not found for emulator '{emulator.Name}'");
                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"FastInstall: Error getting emulator profile for game '{game.Name}'");
                return null;
            }
        }

        public Emulator GetEmulatorForConfig(FolderConfiguration config)
        {
            if (config == null)
            {
                return null;
            }

            if (config.EmulatorId.HasValue && config.EmulatorId.Value != Guid.Empty)
            {
                return PlayniteApi.Database.Emulators.Get(config.EmulatorId.Value);
            }

            // Fallback: try to find RPCS3 for PS3 games if not set
            var emulators = PlayniteApi.Database.Emulators;
            return emulators?.FirstOrDefault(e => e.Name != null && e.Name.IndexOf("rpcs3", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
