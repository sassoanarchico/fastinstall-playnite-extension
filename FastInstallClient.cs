using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Playnite.SDK.Events;
using System.Windows;

namespace FastInstall
{
    public class FastInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly FastInstallPlugin plugin;

        public FastInstallController(Game game, FastInstallPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = ResourceProvider.GetString("LOCFastInstall_Action_InstallFromArchive");
        }

        public override void Install(InstallActionArgs args)
        {
            var sourcePath = plugin.GetSourcePath(Game);
            var destinationPath = plugin.GetDestinationPath(Game);

            // Validate paths before starting
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    ResourceProvider.GetString("LOCFastInstall_Error_SourceArchiveNotConfigured"),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                return;
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    ResourceProvider.GetString("LOCFastInstall_Error_FastInstallDirNotConfigured"),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                return;
            }

            if (!Directory.Exists(sourcePath))
            {
                var fmt = ResourceProvider.GetString("LOCFastInstall_Error_SourceFolderNotFoundFormat");
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    string.Format(fmt, sourcePath),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                return;
            }

            // Check for existing installation and handle conflict
            if (Directory.Exists(destinationPath))
            {
                var conflictResolution = plugin.Settings?.ConflictResolution ?? ConflictResolution.Ask;
                
                if (conflictResolution == ConflictResolution.Skip)
                {
                    var fmt = ResourceProvider.GetString("LOCFastInstall_Message_AlreadyInstalledFormat");
                    plugin.PlayniteApi.Dialogs.ShowMessage(
                        string.Format(fmt, Game.Name, destinationPath),
                        ResourceProvider.GetString("LOCFastInstall_DialogTitle_AlreadyInstalled"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                else if (conflictResolution == ConflictResolution.Ask)
                {
                    var fmt = ResourceProvider.GetString("LOCFastInstall_Message_OverwriteConfirmFormat");
                    var result = plugin.PlayniteApi.Dialogs.ShowMessage(
                        string.Format(fmt, Game.Name, destinationPath),
                        ResourceProvider.GetString("LOCFastInstall_DialogTitle_InstallConflict"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result != MessageBoxResult.Yes)
                    {
                        logger.Info($"FastInstall: User cancelled installation of '{Game.Name}' due to existing installation");
                        return;
                    }
                }
                // If Overwrite, continue with installation
            }

            // Calculate and show disk space requirement
            long requiredBytes = 0;
            long availableBytes = 0;
            bool hasEnoughSpace = FileCopyHelper.CheckDiskSpace(sourcePath, destinationPath, out requiredBytes, out availableBytes);
            
            var requiredFormatted = FileCopyHelper.FormatBytes(requiredBytes);
            var availableFormatted = FileCopyHelper.FormatBytes(availableBytes);
            
            // Show space requirement notification
            plugin.PlayniteApi.Notifications.Add(new NotificationMessage(
                $"FastInstall_SpaceInfo_{Game.Id}",
                string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_SpaceInfoFormat"), Game.Name, requiredFormatted, availableFormatted),
                NotificationType.Info));

            logger.Info($"FastInstall: Starting background installation for '{Game.Name}' (Required: {requiredFormatted}, Available: {availableFormatted})");

            // Start background installation - Playnite remains fully usable!
            BackgroundInstallManager.Instance.StartInstallation(
                Game,
                sourcePath,
                destinationPath,
                (installedArgs) =>
                {
                    // This callback is invoked when installation completes
                    InvokeOnInstalled(installedArgs);
                },
                () =>
                {
                    // This callback is invoked when installation is cancelled
                    InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
                });
        }
    }

    /// <summary>
    /// Install controller for cloud games (Google Drive, etc.)
    /// </summary>
    public class CloudInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly FastInstallPlugin plugin;

        public CloudInstallController(Game game, FastInstallPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = ResourceProvider.GetString("LOCFastInstall_Action_DownloadFromCloud");
        }

        public override void Install(InstallActionArgs args)
        {
            // Get cloud source configuration
            var cloudSource = plugin.GetCloudSourceConfiguration(Game);
            if (cloudSource == null)
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    ResourceProvider.GetString("LOCFastInstall_Error_CloudSourceNotFound"),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                return;
            }

            var destinationPath = cloudSource.DestinationPath;
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    ResourceProvider.GetString("LOCFastInstall_Error_CloudDestinationNotConfigured"),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                return;
            }

            // Get the file ID - first try to extract from game ID (for folder sources)
            var provider = CloudDownloadManager.Instance?.GetProvider(cloudSource.Provider);
            if (provider == null)
            {
                var fmt = ResourceProvider.GetString("LOCFastInstall_Error_CloudProviderNotAvailableFormat");
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    string.Format(fmt, cloudSource.Provider),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                return;
            }

            // Try to extract file ID from game ID (for files from folder sources)
            var fileId = plugin.ExtractFileIdFromGameId(Game.GameId);
            
            // If not found in game ID, parse the cloud link (for direct file links)
            if (string.IsNullOrWhiteSpace(fileId))
            {
                var parseResult = provider.ParseLink(cloudSource.CloudLink);
                if (!parseResult.IsValid)
                {
                    var fmt = ResourceProvider.GetString("LOCFastInstall_Error_InvalidCloudLinkFormat");
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        string.Format(fmt, parseResult.ErrorMessage),
                        ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                    return;
                }
                fileId = parseResult.FileId;
            }
            
            if (string.IsNullOrWhiteSpace(fileId))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    ResourceProvider.GetString("LOCFastInstall_Error_CouldNotDetermineFileId"),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                return;
            }

            // Clean game name (remove [Cloud] suffix)
            var gameName = Game.Name;
            if (gameName.EndsWith(" [Cloud]"))
            {
                gameName = gameName.Substring(0, gameName.Length - 8);
            }

            // Determine if this is likely an archive
            var fileName = !string.IsNullOrWhiteSpace(cloudSource.DisplayName) 
                ? cloudSource.DisplayName 
                : gameName;
            var isArchive = ArchiveHelper.IsArchiveFile(fileName) || 
                           fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                           fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                           fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);

            // Create final destination path
            var finalDestination = Path.Combine(destinationPath, gameName);

            // Check for existing installation
            if (Directory.Exists(finalDestination))
            {
                var conflictResolution = plugin.Settings?.ConflictResolution ?? ConflictResolution.Ask;

                if (conflictResolution == ConflictResolution.Skip)
                {
                    var fmt = ResourceProvider.GetString("LOCFastInstall_Message_AlreadyInstalledFormat");
                    plugin.PlayniteApi.Dialogs.ShowMessage(
                        string.Format(fmt, gameName, finalDestination),
                        ResourceProvider.GetString("LOCFastInstall_DialogTitle_AlreadyInstalled"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                else if (conflictResolution == ConflictResolution.Ask)
                {
                    var fmt = ResourceProvider.GetString("LOCFastInstall_Message_OverwriteConfirmFormat");
                    var result = plugin.PlayniteApi.Dialogs.ShowMessage(
                        string.Format(fmt, gameName, finalDestination),
                        ResourceProvider.GetString("LOCFastInstall_DialogTitle_InstallConflict"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                    {
                        logger.Info($"FastInstall: User cancelled cloud download of '{gameName}' due to existing installation");
                        return;
                    }
                }
            }

            logger.Info($"FastInstall: Starting cloud download for '{gameName}'");
            logger.Info($"FastInstall: FileId: {fileId}");
            logger.Info($"FastInstall: Destination: {finalDestination}");
            logger.Info($"FastInstall: IsArchive: {isArchive}");
            logger.Info($"FastInstall: Provider: {cloudSource.Provider}");

            // Check if CloudDownloadManager is initialized
            if (CloudDownloadManager.Instance == null)
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    ResourceProvider.GetString("LOCFastInstall_Error_CloudDownloadManagerNotInitialized"),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                return;
            }

            // Start cloud download
            try
            {
                CloudDownloadManager.Instance.StartDownload(
                    Game,
                    fileId,
                    fileName,
                    finalDestination,
                    cloudSource.Provider,
                    isArchive,
                    (installedArgs) =>
                    {
                        // Installation completed
                        InvokeOnInstalled(installedArgs);
                    },
                    () =>
                    {
                        // Installation cancelled
                        InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
                    });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FastInstall: Error starting cloud download");
                var fmt = ResourceProvider.GetString("LOCFastInstall_Error_StartDownloadFormat");
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    string.Format(fmt, ex.Message, fileId, finalDestination),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
            }
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
            Name = ResourceProvider.GetString("LOCFastInstall_Action_RemoveFromSsd");
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
                    ResourceProvider.GetString("LOCFastInstall_Error_CouldNotDetermineInstallPath"),
                    ResourceProvider.GetString("LOCFastInstall_Title_Error"));
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
                    var fmt = ResourceProvider.GetString("LOCFastInstall_Error_SafetyCheckFailedFormat");
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        string.Format(fmt, installPath, fastInstallDir),
                        ResourceProvider.GetString("LOCFastInstall_DialogTitle_SafetyError"));
                    return;
                }
            }

            // Calculate size for display
            var sizeToDelete = FileCopyHelper.GetDirectorySize(installPath);
            var sizeFormatted = FileCopyHelper.FormatBytes(sizeToDelete);

            // Confirm deletion
            var confirmFmt = ResourceProvider.GetString("LOCFastInstall_Message_ConfirmUninstallFormat");
            var confirmResult = plugin.PlayniteApi.Dialogs.ShowMessage(
                string.Format(confirmFmt, Game.Name, sizeFormatted, installPath),
                ResourceProvider.GetString("LOCFastInstall_DialogTitle_ConfirmUninstall"),
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
                        var pFmt = ResourceProvider.GetString("LOCFastInstall_Progress_RemovingFormat");
                        progressArgs.Text = string.Format(pFmt, Game.Name, sizeFormatted);
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
                                string.Format(ResourceProvider.GetString("LOCFastInstall_Error_PermissionDeniedUninstallFormat"), Game.Name),
                                ResourceProvider.GetString("LOCFastInstall_DialogTitle_AccessDenied"));
                        });
                    }
                    catch (IOException ex)
                    {
                        logger.Error(ex, $"FastInstall: IO error while uninstalling '{Game.Name}'");
                        plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                                string.Format(ResourceProvider.GetString("LOCFastInstall_Error_CouldNotDeleteFormat"), Game.Name, ex.Message),
                                ResourceProvider.GetString("LOCFastInstall_DialogTitle_DeleteError"));
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"FastInstall: Error uninstalling '{Game.Name}'");
                        plugin.PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                                string.Format(ResourceProvider.GetString("LOCFastInstall_Error_UninstallGenericFormat"), Game.Name, ex.Message),
                                ResourceProvider.GetString("LOCFastInstall_Title_Error"));
                        });
                    }
                },
                new GlobalProgressOptions(string.Format(ResourceProvider.GetString("LOCFastInstall_Progress_RemovingTitleFormat"), Game.Name), false)
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
                    return ResourceProvider.GetString("LOCFastInstall_Play_PlayWithRpcs3");
                case GameType.Switch:
                    return ResourceProvider.GetString("LOCFastInstall_Play_PlayWithRyujinxYuzu");
                case GameType.WiiU:
                    return ResourceProvider.GetString("LOCFastInstall_Play_PlayWithCemu");
                case GameType.Wii:
                case GameType.GameCube:
                    return ResourceProvider.GetString("LOCFastInstall_Play_PlayWithDolphin");
                case GameType.Xbox360:
                    return ResourceProvider.GetString("LOCFastInstall_Play_PlayWithXenia");
                case GameType.PSP:
                    return ResourceProvider.GetString("LOCFastInstall_Play_PlayWithPpsspp");
                case GameType.NDS:
                    return ResourceProvider.GetString("LOCFastInstall_Play_PlayWithMelonDsDesmume");
                case GameType.PC:
                    return ResourceProvider.GetString("LOCFastInstall_Play_Play");
                default:
                    return ResourceProvider.GetString("LOCFastInstall_Play_Play");
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
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Error_LaunchGenericFormat"), Game.Name, ex.Message),
                    ResourceProvider.GetString("LOCFastInstall_DialogTitle_LaunchError"));
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
                    ResourceProvider.GetString("LOCFastInstall_Error_Rpcs3NotConfigured"),
                    ResourceProvider.GetString("LOCFastInstall_DialogTitle_EmulatorNotConfigured"));
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
            // Get emulator profile if configured
            var profile = plugin.GetEmulatorProfileForGame(Game);
            string profileName = "";
            if (profile != null)
            {
                var nameProperty = profile.GetType().GetProperty("Name");
                var name = nameProperty?.GetValue(profile)?.ToString();
                profileName = name != null ? $" (Profile: {name})" : "";
            }
            
            logger.Info($"FastInstall: Launching '{Game.Name}' with emulator '{emulator.Name}'{profileName}");
            logger.Debug($"FastInstall: Emulator ID: {emulator.Id}");
            logger.Debug($"FastInstall: Emulator InstallDir: {emulator.InstallDir ?? "(not set)"}");
            if (profile != null)
            {
                logger.Debug($"FastInstall: Using profile: {profile.Name} (ID: {profile.Id})");
            }

            try
            {
                // Get the game path
                var gamePath = GetGamePathForEmulator();
                logger.Debug($"FastInstall: Game path: {gamePath}");

                // Determine executable from emulator profile (if set) or emulator default
                string executable = null;
                string workingDir = null;
                string arguments = null;

                // If profile is set, use profile's executable/working directory (using reflection)
                if (profile != null)
                {
                    var executableProperty = profile.GetType().GetProperty("Executable");
                    var workingDirProperty = profile.GetType().GetProperty("WorkingDirectory");
                    var argumentsProperty = profile.GetType().GetProperty("Arguments");

                    if (executableProperty != null)
                    {
                        var profileExecutable = executableProperty.GetValue(profile)?.ToString();
                        if (!string.IsNullOrWhiteSpace(profileExecutable))
                        {
                            executable = profileExecutable;
                            if (Path.IsPathRooted(executable) && File.Exists(executable))
                            {
                                workingDir = Path.GetDirectoryName(executable);
                            }
                            else if (!string.IsNullOrWhiteSpace(emulator.InstallDir))
                            {
                                // Try relative to emulator install dir
                                var fullPath = Path.Combine(emulator.InstallDir, executable);
                                if (File.Exists(fullPath))
                                {
                                    executable = fullPath;
                                    workingDir = Path.GetDirectoryName(executable);
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(workingDir) && workingDirProperty != null)
                    {
                        var profileWorkingDir = workingDirProperty.GetValue(profile)?.ToString();
                        if (!string.IsNullOrWhiteSpace(profileWorkingDir))
                        {
                            workingDir = profileWorkingDir;
                            if (!Path.IsPathRooted(workingDir) && !string.IsNullOrWhiteSpace(emulator.InstallDir))
                            {
                                workingDir = Path.Combine(emulator.InstallDir, workingDir);
                            }
                        }
                    }

                    // Use profile arguments if available
                    if (argumentsProperty != null)
                    {
                        var profileArguments = argumentsProperty.GetValue(profile)?.ToString();
                        if (!string.IsNullOrWhiteSpace(profileArguments))
                        {
                            arguments = profileArguments.Replace("{ImagePath}", $"\"{gamePath}\"");
                        }
                    }
                }

                // Fallback to emulator default if profile didn't provide executable
                if (string.IsNullOrWhiteSpace(executable))
                {
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
                }

                // Default arguments if profile didn't provide them
                if (string.IsNullOrWhiteSpace(arguments))
                {
                    arguments = $"\"{gamePath}\"";
                }

                if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                {
                    var fmt = ResourceProvider.GetString("LOCFastInstall_Error_EmulatorExeNotFoundFormat");
                    var profileSuffix = profile != null ? string.Format(ResourceProvider.GetString("LOCFastInstall_Common_ProfileSuffixFormat"), profile.Name) : "";
                    var installDir = emulator.InstallDir ?? ResourceProvider.GetString("LOCFastInstall_Common_NotConfigured");
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                        string.Format(fmt, emulator.Name, profileSuffix, installDir),
                        ResourceProvider.GetString("LOCFastInstall_DialogTitle_EmulatorError"));
                    InvokeOnStopped(new GameStoppedEventArgs());
                    return;
                }

                logger.Info($"FastInstall: Launching with: {executable}");
                logger.Debug($"FastInstall: Arguments: {arguments}");
                logger.Debug($"FastInstall: Working directory: {workingDir ?? "(not set)"}");

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
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Error_LaunchWithEmulatorFormat"), emulator.Name, ex.Message),
                    ResourceProvider.GetString("LOCFastInstall_DialogTitle_LaunchError"));
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
                var preferredNames = new[] { "rpcs3.exe", "xenia.exe", "cemu.exe", "dolphin.exe", "pcsx2.exe", "ppsspp.exe", "melonDS.exe", "DeSmuME.exe" };
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
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Error_GameExeNotFoundFormat"), Game.Name),
                    ResourceProvider.GetString("LOCFastInstall_DialogTitle_ExecutableNotFound"));
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
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Error_GameFileNotFoundFormat"), Game.Name, gameInfo.GameType, gameInfo.PlatformName),
                    ResourceProvider.GetString("LOCFastInstall_DialogTitle_GameFileNotFound"));
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
                        string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_PlayingFormat"), Game.Name),
                        NotificationType.Info));
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"FastInstall: Could not launch '{Game.Name}' with default program");
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Error_NoDefaultProgramFormat"), Game.Name, Path.GetExtension(gameInfo.ExecutablePath)),
                    ResourceProvider.GetString("LOCFastInstall_DialogTitle_LaunchError"));
                InvokeOnStopped(new GameStoppedEventArgs());
            }
        }
    }
}
