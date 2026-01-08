using System;
using System.Diagnostics;
using System.IO;
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
}
