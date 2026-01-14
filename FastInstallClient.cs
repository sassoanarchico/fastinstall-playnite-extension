using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace FastInstall
{
    public class FastInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly FastInstallPlugin plugin;
        private CancellationTokenSource cancellationTokenSource;

        public FastInstallController(Game game, FastInstallPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Install from Archive";
        }

        public override void Install(InstallActionArgs args)
        {
            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;

            var sourcePath = plugin.GetSourcePath(Game);
            var destinationPath = plugin.GetDestinationPath(Game);

            // Validate paths before starting
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    "Source Archive Directory is not configured.\nPlease check FastInstall settings.",
                    "FastInstall Error");
                return;
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    "Fast Install Directory is not configured.\nPlease check FastInstall settings.",
                    "FastInstall Error");
                return;
            }

            if (!Directory.Exists(sourcePath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    $"Source folder not found:\n{sourcePath}\n\nThe game may have been moved or deleted.",
                    "FastInstall Error");
                return;
            }

            // Ensure destination parent directory exists
            var destParent = Path.GetDirectoryName(destinationPath);
            if (!Directory.Exists(destParent))
            {
                try
                {
                    Directory.CreateDirectory(destParent);
                }
                catch (Exception ex)
                {
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        $"Could not create destination folder:\n{destParent}\n\nError: {ex.Message}",
                        "FastInstall Error");
                    return;
                }
            }

            logger.Info($"FastInstall: Installing '{Game.Name}' from '{sourcePath}' to '{destinationPath}'");

            // Run the copy with progress dialog
            var globalProgress = plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(
                (progressArgs) =>
                {
                    try
                    {
                        progressArgs.ProgressMaxValue = 100;
                        progressArgs.CurrentProgressValue = 0;
                        progressArgs.Text = $"Preparing to copy {Game.Name}...";

                        var copyTask = FileCopyHelper.CopyDirectoryWithProgress(
                            sourcePath,
                            destinationPath,
                            (progress) =>
                            {
                                // Update progress dialog
                                progressArgs.CurrentProgressValue = progress.PercentComplete;
                                progressArgs.Text = FormatProgressText(Game.Name, progress);
                            },
                            progressArgs.CancelToken);

                        copyTask.Wait(progressArgs.CancelToken);

                        if (progressArgs.CancelToken.IsCancellationRequested)
                        {
                            // Clean up partial copy
                            CleanupPartialCopy(destinationPath);
                            return;
                        }

                        if (copyTask.Result)
                        {
                            // Success - notify on main thread
                            plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                            {
                                InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData
                                {
                                    InstallDirectory = destinationPath
                                }));
                            });

                            logger.Info($"FastInstall: Successfully installed '{Game.Name}'");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        logger.Info($"FastInstall: Installation of '{Game.Name}' was cancelled.");
                        CleanupPartialCopy(destinationPath);
                    }
                    catch (AggregateException ae)
                    {
                        var innerEx = ae.InnerException ?? ae;
                        HandleCopyException(innerEx, destinationPath);
                    }
                    catch (Exception ex)
                    {
                        HandleCopyException(ex, destinationPath);
                    }
                },
                new GlobalProgressOptions($"Installing {Game.Name}...", true)
                {
                    IsIndeterminate = false
                });
        }

        private string FormatProgressText(string gameName, CopyProgressInfo progress)
        {
            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"Installing: {gameName}");
            lines.AppendLine();
            lines.AppendLine($"Progress: {progress.CopiedFormatted} / {progress.TotalFormatted} ({progress.PercentComplete}%)");
            lines.AppendLine($"Speed: {progress.SpeedFormatted}");
            lines.AppendLine($"Elapsed: {progress.ElapsedFormatted}");
            lines.AppendLine($"Remaining: {progress.RemainingFormatted}");
            lines.AppendLine();
            lines.AppendLine($"Files: {progress.FilesCopied} / {progress.TotalFiles}");
            
            if (!string.IsNullOrEmpty(progress.CurrentFile))
            {
                var displayFile = progress.CurrentFile;
                if (displayFile.Length > 50)
                {
                    displayFile = "..." + displayFile.Substring(displayFile.Length - 47);
                }
                lines.AppendLine($"Current: {displayFile}");
            }

            return lines.ToString();
        }

        private void CleanupPartialCopy(string destinationPath)
        {
            try
            {
                if (Directory.Exists(destinationPath))
                {
                    logger.Info($"FastInstall: Cleaning up partial copy at '{destinationPath}'");
                    Directory.Delete(destinationPath, true);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"FastInstall: Could not clean up partial copy at '{destinationPath}'");
            }
        }

        private void HandleCopyException(Exception ex, string destinationPath)
        {
            CleanupPartialCopy(destinationPath);

            if (ex is IOException ioEx && (ioEx.Message.Contains("not enough space") || ioEx.HResult == -2147024784))
            {
                logger.Error(ex, $"FastInstall: Disk full while installing '{Game.Name}'");
                plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        $"Not enough disk space to install '{Game.Name}'.\n\nPlease free up space on the destination drive and try again.",
                        "FastInstall - Disk Full");
                });
            }
            else if (ex is UnauthorizedAccessException)
            {
                logger.Error(ex, $"FastInstall: Permission denied while installing '{Game.Name}'");
                plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        $"Permission denied while installing '{Game.Name}'.\n\nPlease check folder permissions and ensure no files are in use.",
                        "FastInstall - Access Denied");
                });
            }
            else if (ex is DirectoryNotFoundException)
            {
                logger.Error(ex, $"FastInstall: Source folder missing for '{Game.Name}'");
                plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        $"Source folder not found for '{Game.Name}'.\n\nThe game may have been moved or deleted from the archive.",
                        "FastInstall - Source Missing");
                });
            }
            else
            {
                logger.Error(ex, $"FastInstall: Error installing '{Game.Name}'");
                plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        $"Error installing '{Game.Name}':\n\n{ex.Message}",
                        "FastInstall Error");
                });
            }
        }

        public override void Dispose()
        {
            cancellationTokenSource?.Cancel();
            cancellationTokenSource?.Dispose();
            base.Dispose();
        }
    }

    public class FastUninstallController : UninstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly FastInstallPlugin plugin;
        private CancellationTokenSource cancellationTokenSource;

        public FastUninstallController(Game game, FastInstallPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Remove from SSD";
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            cancellationTokenSource = new CancellationTokenSource();

            var installPath = Game.InstallDirectory;

            if (string.IsNullOrWhiteSpace(installPath))
            {
                installPath = plugin.GetDestinationPath(Game);
            }

            if (string.IsNullOrWhiteSpace(installPath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    "Could not determine install path for uninstallation.",
                    "FastInstall Error");
                return;
            }

            if (!Directory.Exists(installPath))
            {
                logger.Warn($"FastInstall: Install directory not found for '{Game.Name}': {installPath}");
                // Still mark as uninstalled since the folder doesn't exist
                InvokeOnUninstalled(new GameUninstalledEventArgs());
                return;
            }

            // Safety check: make sure we're deleting from the FastInstall directory
            var fastInstallDir = plugin.Settings?.FastInstallDirectory;
            if (!string.IsNullOrWhiteSpace(fastInstallDir))
            {
                var normalizedInstallPath = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar);
                var normalizedFastInstallDir = Path.GetFullPath(fastInstallDir).TrimEnd(Path.DirectorySeparatorChar);

                if (!normalizedInstallPath.StartsWith(normalizedFastInstallDir, StringComparison.OrdinalIgnoreCase))
                {
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        $"Safety check failed: Will only delete from FastInstall directory.\n\n" +
                        $"Install path '{installPath}' is not within '{fastInstallDir}'.",
                        "FastInstall - Safety Error");
                    return;
                }
            }

            // Calculate size for display
            var sizeToDelete = FileCopyHelper.GetDirectorySize(installPath);
            var sizeFormatted = FileCopyHelper.FormatBytes(sizeToDelete);

            // Confirm deletion
            var confirmResult = plugin.PlayniteApi.Dialogs.ShowMessage(
                $"Are you sure you want to remove '{Game.Name}' from the SSD?\n\n" +
                $"This will delete {sizeFormatted} from:\n{installPath}\n\n" +
                $"The original archive will NOT be affected.",
                "FastInstall - Confirm Uninstall",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (confirmResult != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            logger.Info($"FastInstall: Uninstalling '{Game.Name}' from '{installPath}'");

            // Run deletion with progress
            plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(
                (progressArgs) =>
                {
                    try
                    {
                        progressArgs.Text = $"Removing {Game.Name} ({sizeFormatted})...";
                        progressArgs.IsIndeterminate = true;

                        // Delete the directory
                        Directory.Delete(installPath, true);

                        // Notify Playnite that uninstallation is complete
                        plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            InvokeOnUninstalled(new GameUninstalledEventArgs());
                        });

                        logger.Info($"FastInstall: Successfully uninstalled '{Game.Name}'");
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        logger.Error(ex, $"FastInstall: Permission denied while uninstalling '{Game.Name}'");
                        plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                                $"Permission denied while uninstalling '{Game.Name}'.\n\nSome files may be in use. Please close any programs using files in this folder.",
                                "FastInstall - Access Denied");
                        });
                    }
                    catch (IOException ex)
                    {
                        logger.Error(ex, $"FastInstall: IO error while uninstalling '{Game.Name}'");
                        plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                                $"Could not delete '{Game.Name}':\n\n{ex.Message}\n\nSome files may be in use.",
                                "FastInstall - Delete Error");
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"FastInstall: Error uninstalling '{Game.Name}'");
                        plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                                $"Error uninstalling '{Game.Name}':\n\n{ex.Message}",
                                "FastInstall Error");
                        });
                    }
                },
                new GlobalProgressOptions($"Removing {Game.Name}...", false)
                {
                    IsIndeterminate = true
                });
        }

        public override void Dispose()
        {
            cancellationTokenSource?.Cancel();
            cancellationTokenSource?.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Play controller that launches games with the appropriate emulator
    /// </summary>
    public class FastInstallPlayController : PlayController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly FastInstallPlugin plugin;
        private readonly DetectedGameInfo gameInfo;
        private Process emulatorProcess;
        private Stopwatch playStopwatch;

        public FastInstallPlayController(Game game, FastInstallPlugin plugin, DetectedGameInfo gameInfo) : base(game)
        {
            this.plugin = plugin;
            this.gameInfo = gameInfo;
            Name = GetPlayActionName();
        }

        private string GetPlayActionName()
        {
            switch (gameInfo.GameType)
            {
                case GameType.PS3:
                    return "Play with RPCS3";
                case GameType.Switch:
                    return "Play with Ryujinx/Yuzu";
                case GameType.WiiU:
                    return "Play with Cemu";
                case GameType.Wii:
                case GameType.GameCube:
                    return "Play with Dolphin";
                case GameType.Xbox360:
                    return "Play with Xenia";
                case GameType.PSP:
                    return "Play with PPSSPP";
                case GameType.PC:
                    return "Play";
                default:
                    return "Play";
            }
        }

        public override void Play(PlayActionArgs args)
        {
            playStopwatch = Stopwatch.StartNew();

            try
            {
                switch (gameInfo.GameType)
                {
                    case GameType.PS3:
                        LaunchPS3Game();
                        break;
                    case GameType.PC:
                        LaunchPCGame();
                        break;
                    default:
                        // For other types, try to launch with default program or show message
                        LaunchWithDefaultProgram();
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"FastInstall: Error launching '{Game.Name}'");
                InvokeOnStopped(new GameStoppedEventArgs());
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    $"Error launching '{Game.Name}':\n\n{ex.Message}",
                    "FastInstall - Launch Error");
            }
        }

        private void LaunchPS3Game()
        {
            // Try to get emulator from configuration
            var emulator = plugin.GetEmulatorForGame(Game);

            if (emulator != null)
            {
                // Use the configured emulator from Playnite
                LaunchWithEmulator(emulator);
                return;
            }

            // Fallback: Try to find RPCS3 manually
            var rpcs3Path = FindRPCS3Path();

            if (string.IsNullOrWhiteSpace(rpcs3Path) || !File.Exists(rpcs3Path))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    "RPCS3 emulator not configured.\n\n" +
                    "Please configure an emulator:\n" +
                    "1. Go to Playnite ? Settings ? Emulation\n" +
                    "2. Add RPCS3 emulator\n" +
                    "3. Return to FastInstall settings and select RPCS3 from the Emulator dropdown",
                    "FastInstall - Emulator Not Configured");
                InvokeOnStopped(new GameStoppedEventArgs());
                return;
            }

            // Launch with manually found RPCS3
            LaunchWithRPCS3Path(rpcs3Path);
        }

        private string FindRPCS3Path()
        {
            // Try to find RPCS3 in Playnite's emulators first
            try
            {
                var rpcs3Emulators = plugin.PlayniteApi.Database.Emulators
                    .Where(e => e.Name.ToLowerInvariant().Contains("rpcs3"))
                    .ToList();

                foreach (var emu in rpcs3Emulators)
                {
                    if (!string.IsNullOrWhiteSpace(emu.InstallDir) && Directory.Exists(emu.InstallDir))
                    {
                        var exePath = Path.Combine(emu.InstallDir, "rpcs3.exe");
                        if (File.Exists(exePath))
                            return exePath;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "FastInstall: Error searching for RPCS3 in Playnite emulators");
            }

            // Try common locations
            var commonPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RPCS3", "rpcs3.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RPCS3", "rpcs3.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RPCS3", "rpcs3.exe"),
                @"C:\RPCS3\rpcs3.exe",
                @"D:\RPCS3\rpcs3.exe",
                @"E:\RPCS3\rpcs3.exe"
            };

            foreach (var path in commonPaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private void LaunchWithEmulator(Playnite.SDK.Models.Emulator emulator)
        {
            logger.Info($"FastInstall: Launching '{Game.Name}' with emulator '{emulator.Name}'");
            logger.Debug($"FastInstall: Emulator ID: {emulator.Id}");
            logger.Debug($"FastInstall: Emulator InstallDir: {emulator.InstallDir ?? "(not set)"}");

            try
            {
                // Get the game path
                var gamePath = GetGamePathForEmulator();
                logger.Debug($"FastInstall: Game path: {gamePath}");

                // Determine executable from emulator
                string executable = null;
                string workingDir = null;

                if (!string.IsNullOrWhiteSpace(emulator.InstallDir))
                {
                    logger.Debug($"FastInstall: Searching for executable in: {emulator.InstallDir}");
                    if (Directory.Exists(emulator.InstallDir))
                    {
                        executable = FindEmulatorExecutable(emulator.InstallDir);
                        logger.Debug($"FastInstall: Found executable: {executable ?? "(not found)"}");
                        if (!string.IsNullOrWhiteSpace(executable))
                        {
                            workingDir = Path.GetDirectoryName(executable);
                        }
                    }
                    else
                    {
                        logger.Warn($"FastInstall: Emulator InstallDir does not exist: {emulator.InstallDir}");
                    }
                }
                else
                {
                    logger.Warn($"FastInstall: Emulator InstallDir is not configured");
                }

                if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                {
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        $"Could not find executable for emulator '{emulator.Name}'.\n\n" +
                        $"Install directory: {emulator.InstallDir ?? "(not configured)"}\n\n" +
                        "Please verify the emulator is properly installed and configured in Playnite settings.",
                        "FastInstall - Emulator Error");
                    InvokeOnStopped(new GameStoppedEventArgs());
                    return;
                }

                var arguments = $"\"{gamePath}\"";

                logger.Info($"FastInstall: Launching with: {executable}");
                logger.Debug($"FastInstall: Arguments: {arguments}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false
                };

                emulatorProcess = Process.Start(startInfo);

                if (emulatorProcess != null)
                {
                    logger.Info($"FastInstall: Emulator process started (PID: {emulatorProcess.Id})");
                    InvokeOnStarted(new GameStartedEventArgs());

                    Task.Run(() =>
                    {
                        emulatorProcess.WaitForExit();
                        playStopwatch?.Stop();
                        logger.Info($"FastInstall: Emulator process exited after {playStopwatch?.Elapsed.TotalSeconds ?? 0} seconds");

                        plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            InvokeOnStopped(new GameStoppedEventArgs(Convert.ToUInt64(playStopwatch?.Elapsed.TotalSeconds ?? 0)));
                        });
                    });
                }
                else
                {
                    logger.Error($"FastInstall: Failed to start emulator process");
                    InvokeOnStopped(new GameStoppedEventArgs());
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"FastInstall: Error launching game with emulator '{emulator.Name}'");
                InvokeOnStopped(new GameStoppedEventArgs());
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    $"Error launching with '{emulator.Name}':\n\n{ex.Message}",
                    "FastInstall - Launch Error");
            }
        }

        private string GetGamePathForEmulator()
        {
            logger.Info($"FastInstall: GetGamePathForEmulator called for '{Game.Name}'");
            logger.Debug($"FastInstall: Game.InstallDirectory = '{Game.InstallDirectory ?? "(null)"}'");
            logger.Debug($"FastInstall: gameInfo.FolderPath = '{gameInfo?.FolderPath ?? "(null)"}'");
            logger.Debug($"FastInstall: gameInfo.ExecutablePath = '{gameInfo?.ExecutablePath ?? "(null)"}'");
            logger.Debug($"FastInstall: gameInfo.GameType = {gameInfo?.GameType}");
            
            // Try multiple sources to find the game path
            var possiblePaths = new System.Collections.Generic.List<string>();
            
            // 1. First try Game.InstallDirectory (the installed location)
            if (!string.IsNullOrWhiteSpace(Game.InstallDirectory))
            {
                possiblePaths.Add(Game.InstallDirectory);
            }
            
            // 2. Try the destination path from plugin configuration
            var destPath = plugin.GetDestinationPath(Game);
            if (!string.IsNullOrWhiteSpace(destPath))
            {
                possiblePaths.Add(destPath);
            }
            
            // 3. Try gameInfo paths
            if (!string.IsNullOrWhiteSpace(gameInfo?.FolderPath))
            {
                possiblePaths.Add(gameInfo.FolderPath);
            }
            
            // 4. Try source path as last resort
            var sourcePath = plugin.GetSourcePath(Game);
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                possiblePaths.Add(sourcePath);
            }
            
            logger.Debug($"FastInstall: Possible paths to check: {string.Join(", ", possiblePaths)}");
            
            // For PS3 games, RPCS3 boots best from the folder containing PS3_GAME
            if (gameInfo?.GameType == GameType.PS3)
            {
                foreach (var basePath in possiblePaths)
                {
                    if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath))
                    {
                        logger.Debug($"FastInstall: Path doesn't exist, skipping: {basePath}");
                        continue;
                    }
                    
                    logger.Debug($"FastInstall: Checking path: {basePath}");
                    
                    // Check if PS3_GAME exists directly in this folder
                    var ps3GameFolder = Path.Combine(basePath, "PS3_GAME");
                    if (Directory.Exists(ps3GameFolder))
                    {
                        logger.Info($"FastInstall: Found PS3_GAME folder at: {basePath}");
                        return basePath; // Return the folder containing PS3_GAME
                    }
                    
                    // Search for PS3_GAME folder recursively (handles nested folders)
                    try
                    {
                        var ps3GameFolders = Directory.GetDirectories(basePath, "PS3_GAME", SearchOption.AllDirectories);
                        if (ps3GameFolders.Length > 0)
                        {
                            // Return the parent folder of PS3_GAME
                            var gameRootFolder = Path.GetDirectoryName(ps3GameFolders[0]);
                            logger.Info($"FastInstall: Found PS3_GAME via search, using game folder: {gameRootFolder}");
                            return gameRootFolder;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, $"FastInstall: Error searching for PS3_GAME in {basePath}");
                    }
                    
                    // Fallback: search for EBOOT.BIN and return its grandparent folder
                    try
                    {
                        var ebootFiles = Directory.GetFiles(basePath, "EBOOT.BIN", SearchOption.AllDirectories);
                        if (ebootFiles.Length > 0)
                        {
                            // EBOOT.BIN is typically in PS3_GAME/USRDIR/, so go up 2 levels
                            var ebootDir = Path.GetDirectoryName(ebootFiles[0]); // USRDIR
                            var ps3GameDir = Path.GetDirectoryName(ebootDir);     // PS3_GAME
                            var gameRoot = Path.GetDirectoryName(ps3GameDir);     // Game root
                            
                            if (!string.IsNullOrEmpty(gameRoot) && Directory.Exists(gameRoot))
                            {
                                logger.Info($"FastInstall: Found EBOOT.BIN, using game folder: {gameRoot}");
                                return gameRoot;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, $"FastInstall: Error searching for EBOOT.BIN in {basePath}");
                    }
                }
                
                // Fallback: return the first valid folder path
                foreach (var basePath in possiblePaths)
                {
                    if (!string.IsNullOrEmpty(basePath) && Directory.Exists(basePath))
                    {
                        logger.Info($"FastInstall: Using folder path for RPCS3 (no PS3_GAME found): {basePath}");
                        return basePath;
                    }
                }
            }
            
            // For non-PS3 games or if nothing found
            if (!string.IsNullOrEmpty(gameInfo?.ExecutablePath) && File.Exists(gameInfo.ExecutablePath))
            {
                logger.Info($"FastInstall: Using gameInfo.ExecutablePath: {gameInfo.ExecutablePath}");
                return gameInfo.ExecutablePath;
            }

            // Final fallback
            var fallback = Game.InstallDirectory ?? gameInfo?.FolderPath;
            logger.Info($"FastInstall: Using fallback path: {fallback ?? "(null)"}");
            return fallback;
        }

        private string FindEmulatorExecutable(string installDir)
        {
            // Look for common emulator executables            
            try
            {
                var exeFiles = Directory.GetFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly);
                if (exeFiles.Length == 1)
                    return exeFiles[0];

                // Prefer certain names
                var preferredNames = new[] { "rpcs3.exe", "xenia.exe", "cemu.exe", "dolphin.exe", "pcsx2.exe", "ppsspp.exe" };
                foreach (var preferred in preferredNames)
                {
                    var found = exeFiles.Where(f => Path.GetFileName(f).Equals(preferred, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                    if (found != null)
                        return found;
                }

                // Return first exe if multiple found
                if (exeFiles.Length > 0)
                    return exeFiles[0];
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"FastInstall: Error finding emulator executable in '{installDir}'");
            }

            return null;
        }

        private void LaunchWithRPCS3Path(string rpcs3Path)
        {
            // Get the game path - for PS3 we pass the folder containing PS3_GAME
            var gamePath = Game.InstallDirectory;

            // RPCS3 can be launched with the EBOOT.BIN path or the game folder
            if (!string.IsNullOrEmpty(gameInfo.ExecutablePath) && File.Exists(gameInfo.ExecutablePath))
            {
                gamePath = gameInfo.ExecutablePath;
            }

            logger.Info($"FastInstall: Launching PS3 game '{Game.Name}' with RPCS3");
            logger.Debug($"FastInstall: RPCS3 path: {rpcs3Path}");
            logger.Debug($"FastInstall: Game path: {gamePath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = rpcs3Path,
                Arguments = $"\"{gamePath}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(rpcs3Path)
            };

            emulatorProcess = Process.Start(startInfo);

            if (emulatorProcess != null)
            {
                InvokeOnStarted(new GameStartedEventArgs());

                Task.Run(() =>
                {
                    emulatorProcess.WaitForExit();
                    playStopwatch?.Stop();

                    plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        InvokeOnStopped(new GameStoppedEventArgs(Convert.ToUInt64(playStopwatch?.Elapsed.TotalSeconds ?? 0)));
                    });
                });
            }
            else
            {
                InvokeOnStopped(new GameStoppedEventArgs());
            }
        }

        private void LaunchPCGame()
        {
            if (string.IsNullOrWhiteSpace(gameInfo.ExecutablePath) || !File.Exists(gameInfo.ExecutablePath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    $"Could not find game executable for '{Game.Name}'.\n\n" +
                    "Please verify the game files are present in the install directory.",
                    "FastInstall - Executable Not Found");
                InvokeOnStopped(new GameStoppedEventArgs());
                return;
            }

            logger.Info($"FastInstall: Launching PC game '{Game.Name}'");
            logger.Debug($"FastInstall: Executable: {gameInfo.ExecutablePath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = gameInfo.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(gameInfo.ExecutablePath),
                UseShellExecute = true
            };

            emulatorProcess = Process.Start(startInfo);

            if (emulatorProcess != null)
            {
                InvokeOnStarted(new GameStartedEventArgs());

                Task.Run(() =>
                {
                    emulatorProcess.WaitForExit();
                    playStopwatch?.Stop();

                    plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        InvokeOnStopped(new GameStoppedEventArgs(Convert.ToUInt64(playStopwatch?.Elapsed.TotalSeconds ?? 0)));
                    });
                });
            }
            else
            {
                InvokeOnStopped(new GameStoppedEventArgs());
            }
        }

        private void LaunchWithDefaultProgram()
        {
            if (string.IsNullOrWhiteSpace(gameInfo.ExecutablePath) || !File.Exists(gameInfo.ExecutablePath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    $"Could not find game file for '{Game.Name}' ({gameInfo.GameType}).\n\n" +
                    $"Platform: {gameInfo.PlatformName}\n" +
                    "Please configure the appropriate emulator in Playnite or set up a custom play action.",
                    "FastInstall - Game File Not Found");
                InvokeOnStopped(new GameStoppedEventArgs());
                return;
            }

            logger.Info($"FastInstall: Launching '{Game.Name}' with default program");
            logger.Debug($"FastInstall: File: {gameInfo.ExecutablePath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = gameInfo.ExecutablePath,
                UseShellExecute = true
            };

            try
            {
                emulatorProcess = Process.Start(startInfo);

                if (emulatorProcess != null)
                {
                    InvokeOnStarted(new GameStartedEventArgs());

                    Task.Run(() =>
                    {
                        emulatorProcess.WaitForExit();
                        playStopwatch?.Stop();

                        plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            InvokeOnStopped(new GameStoppedEventArgs(Convert.ToUInt64(playStopwatch?.Elapsed.TotalSeconds ?? 0)));
                        });
                    });
                }
                else
                {
                    InvokeOnStopped(new GameStoppedEventArgs());

                    plugin.PlayniteApi.Notifications.Add(new NotificationMessage(
                        $"FastInstall_Playing_{Game.Id}",
                        $"'{Game.Name}' is now playing.\nYou may need to manually stop the game in Playnite when done.",
                        NotificationType.Info));
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"FastInstall: Could not launch '{Game.Name}' with default program");
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    $"Could not launch '{Game.Name}'.\n\n" +
                    $"No default program is associated with {Path.GetExtension(gameInfo.ExecutablePath)} files.\n\n" +
                    "Please install the appropriate emulator and set it as the default program, or configure a custom play action.",
                    "FastInstall - Launch Error");
                InvokeOnStopped(new GameStoppedEventArgs());
            }
        }
    }
}
