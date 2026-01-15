using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace FastInstall
{
    /// <summary>
    /// Represents an active installation job
    /// </summary>
    public class InstallationJob
    {
        public Game Game { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public InstallationProgressWindow ProgressWindow { get; set; }
        public Action<GameInstalledEventArgs> OnInstalled { get; set; }
        public Action OnCancelled { get; set; }
        public DateTime StartTime { get; set; }
        public InstallationStatus Status { get; set; }
    }

    public enum InstallationStatus
    {
        Pending,
        InProgress,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Manages background installations without blocking Playnite UI
    /// </summary>
    public class BackgroundInstallManager
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static BackgroundInstallManager instance;
        private static readonly object lockObj = new object();
        
        private readonly ConcurrentDictionary<Guid, InstallationJob> activeInstallations;
        private readonly ConcurrentQueue<InstallationJob> installQueue;
        private readonly IPlayniteAPI playniteApi;
        private bool isProcessingQueue;

        public static BackgroundInstallManager Instance
        {
            get
            {
                if (instance == null)
                {
                    throw new InvalidOperationException("BackgroundInstallManager not initialized. Call Initialize() first.");
                }
                return instance;
            }
        }

        public static void Initialize(IPlayniteAPI api)
        {
            lock (lockObj)
            {
                if (instance == null)
                {
                    instance = new BackgroundInstallManager(api);
                }
            }
        }

        private BackgroundInstallManager(IPlayniteAPI api)
        {
            playniteApi = api;
            activeInstallations = new ConcurrentDictionary<Guid, InstallationJob>();
            installQueue = new ConcurrentQueue<InstallationJob>();
            isProcessingQueue = false;
        }

        /// <summary>
        /// Checks if a game is currently being installed
        /// </summary>
        public bool IsInstalling(Guid gameId)
        {
            return activeInstallations.ContainsKey(gameId);
        }

        /// <summary>
        /// Starts a background installation for a game
        /// </summary>
        public void StartInstallation(
            Game game,
            string sourcePath,
            string destinationPath,
            Action<GameInstalledEventArgs> onInstalled,
            Action onCancelled = null)
        {
            if (IsInstalling(game.Id))
            {
                playniteApi.Dialogs.ShowMessage(
                    $"'{game.Name}' is already being installed.",
                    "Installation in Progress",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var cts = new CancellationTokenSource();
            
            var job = new InstallationJob
            {
                Game = game,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                CancellationTokenSource = cts,
                OnInstalled = onInstalled,
                OnCancelled = onCancelled,
                StartTime = DateTime.Now,
                Status = InstallationStatus.Pending
            };

            activeInstallations[game.Id] = job;

            bool willStartImmediately;
            lock (lockObj)
            {
                // If nothing is processing and queue is empty, this job will start right away.
                willStartImmediately = !isProcessingQueue && installQueue.IsEmpty;
                installQueue.Enqueue(job);

                if (!isProcessingQueue)
                {
                    isProcessingQueue = true;
                    Task.Run(ProcessQueue);
                }
            }

            // Create and show progress window on UI thread
            playniteApi.MainView.UIDispatcher.Invoke(() =>
            {
                job.ProgressWindow = new InstallationProgressWindow(
                    game.Name,
                    cts,
                    () => CancelInstallation(game.Id),
                    startQueued: !willStartImmediately);
                
                job.ProgressWindow.Show();
            });

            logger.Info($"FastInstall: Queued background installation for '{game.Name}' (willStartImmediately={willStartImmediately})");
        }

        /// <summary>
        /// Processes installation jobs sequentially from the queue.
        /// </summary>
        private async Task ProcessQueue()
        {
            try
            {
                while (installQueue.TryDequeue(out var job))
                {
                    // If user cancelled before this job started, mark as cancelled and update UI.
                    if (job.CancellationTokenSource.IsCancellationRequested)
                    {
                        job.Status = InstallationStatus.Cancelled;
                        
                        // Remove from active installations immediately
                        activeInstallations.TryRemove(job.Game.Id, out _);
                        
                        // Update UI - show cancelled but don't show "Cleaning up" since nothing was copied
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            if (job.ProgressWindow != null)
                            {
                                job.ProgressWindow.StatusText.Text = "Installation cancelled";
                                job.ProgressWindow.StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                                job.ProgressWindow.CancelButton.Content = "Close";
                                job.ProgressWindow.CancelButton.Background = System.Windows.Media.Brushes.Gray;
                                job.ProgressWindow.AllowClose();
                            }
                        });
                        
                        // Notify controller that installation was cancelled
                        job.OnCancelled?.Invoke();
                        
                        // Close the progress window automatically after a short delay (2 seconds)
                        _ = Task.Delay(2000).ContinueWith(_ =>
                        {
                            playniteApi.MainView.UIDispatcher.Invoke(() =>
                            {
                                job.ProgressWindow?.Close();
                            });
                        });
                        
                        logger.Info($"FastInstall: Skipping queued job for '{job.Game.Name}' because it was cancelled before start.");
                        continue;
                    }

                    logger.Info($"FastInstall: Starting queued installation for '{job.Game.Name}'");
                    await ExecuteInstallation(job);
                }
            }
            finally
            {
                lock (lockObj)
                {
                    isProcessingQueue = false;
                }
            }
        }

        private async Task ExecuteInstallation(InstallationJob job)
        {
            job.Status = InstallationStatus.InProgress;

            try
            {
                logger.Info($"FastInstall: Installing '{job.Game.Name}' from '{job.SourcePath}' to '{job.DestinationPath}'");

                // Check disk space before starting installation
                bool hasEnoughSpace = FileCopyHelper.CheckDiskSpace(
                    job.SourcePath,
                    job.DestinationPath,
                    out long requiredBytes,
                    out long availableBytes);

                if (!hasEnoughSpace)
                {
                    var requiredFormatted = FileCopyHelper.FormatBytes(requiredBytes);
                    var availableFormatted = FileCopyHelper.FormatBytes(availableBytes);
                    var missingFormatted = FileCopyHelper.FormatBytes(requiredBytes - availableBytes);

                    logger.Warn($"FastInstall: Insufficient disk space. Required: {requiredFormatted}, Available: {availableFormatted}");

                    // Show warning dialog on UI thread and wait for user response
                    var userResponse = await Task.Run(() =>
                    {
                        var dialogResult = MessageBoxResult.No;
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            dialogResult = MessageBox.Show(
                                $"Spazio su disco insufficiente!\n\n" +
                                $"Gioco: {job.Game.Name}\n" +
                                $"Spazio richiesto: {requiredFormatted}\n" +
                                $"Spazio disponibile: {availableFormatted}\n" +
                                $"Spazio mancante: {missingFormatted}\n\n" +
                                $"Vuoi continuare comunque? L'installazione potrebbe fallire.",
                                "FastInstall - Spazio su disco insufficiente",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning);
                        });
                        return dialogResult;
                    });

                    if (userResponse == MessageBoxResult.No)
                    {
                        job.Status = InstallationStatus.Cancelled;
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.ShowError("Installazione annullata: spazio su disco insufficiente");
                            job.ProgressWindow?.AllowClose();
                        });

                        playniteApi.Notifications.Add(new NotificationMessage(
                            $"FastInstall_InsufficientSpace_{job.Game.Id}",
                            $"Installazione di '{job.Game.Name}' annullata: spazio su disco insufficiente",
                            NotificationType.Error));

                        logger.Info($"FastInstall: Installation of '{job.Game.Name}' cancelled due to insufficient disk space");

                        // Notify controller that installation was cancelled
                        job.OnCancelled?.Invoke();
                        return;
                    }

                    logger.Info($"FastInstall: User chose to continue despite insufficient disk space");
                }
                else
                {
                    logger.Info($"FastInstall: Disk space check passed. Required: {FileCopyHelper.FormatBytes(requiredBytes)}, Available: {FileCopyHelper.FormatBytes(availableBytes)}");
                }

                // Ensure destination parent directory exists
                var destParent = Path.GetDirectoryName(job.DestinationPath);
                if (!Directory.Exists(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }

                // Copy with progress
                var result = await FileCopyHelper.CopyDirectoryWithProgress(
                    job.SourcePath,
                    job.DestinationPath,
                    (progress) =>
                    {
                        // Update UI on dispatcher thread
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.UpdateProgress(progress);
                        });
                    },
                    job.CancellationTokenSource.Token);

                if (job.CancellationTokenSource.Token.IsCancellationRequested)
                {
                    job.Status = InstallationStatus.Cancelled;
                    
                    // Only cleanup if we actually started copying (destination might exist)
                    bool needsCleanup = Directory.Exists(job.DestinationPath);
                    if (needsCleanup)
                    {
                        CleanupPartialCopy(job.DestinationPath);
                    }
                    
                    playniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        job.ProgressWindow?.ShowCancelled(showCleaningUp: needsCleanup);
                        job.ProgressWindow?.AllowClose();
                    });

                    logger.Info($"FastInstall: Installation of '{job.Game.Name}' was cancelled");

                    // Close the progress window automatically after a short delay (2 seconds)
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.Close();
                        });
                    });

                    // Notify controller that installation was cancelled
                    job.OnCancelled?.Invoke();
                }
                else if (result)
                {
                    // Perform integrity check after copy
                    logger.Info($"FastInstall: Starting integrity check for '{job.Game.Name}'...");
                    
                    playniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = "Verifica integrità file...";
                        }
                    });

                    var integrityResult = await Task.Run(() =>
                    {
                        return FileCopyHelper.VerifyCopyIntegrity(
                            job.SourcePath,
                            job.DestinationPath,
                            (message) =>
                            {
                                playniteApi.MainView.UIDispatcher.Invoke(() =>
                                {
                                    if (job.ProgressWindow != null)
                                    {
                                        job.ProgressWindow.StatusText.Text = message;
                                    }
                                });
                            },
                            job.CancellationTokenSource.Token);
                    });

                    if (!integrityResult.IsValid)
                    {
                        var errorDetails = new System.Text.StringBuilder();
                        errorDetails.AppendLine($"Verifica integrità fallita per '{job.Game.Name}':");
                        
                        if (integrityResult.MissingFiles > 0)
                        {
                            errorDetails.AppendLine($"File mancanti: {integrityResult.MissingFiles}");
                            if (integrityResult.MissingFilePaths.Count > 0)
                            {
                                errorDetails.AppendLine($"Primi file mancanti: {string.Join(", ", integrityResult.MissingFilePaths.Take(5))}");
                            }
                        }
                        
                        if (integrityResult.MismatchedFiles > 0)
                        {
                            errorDetails.AppendLine($"File con dimensioni diverse: {integrityResult.MismatchedFiles}");
                            if (integrityResult.MismatchedFilePaths.Count > 0)
                            {
                                errorDetails.AppendLine($"Primi file con problemi: {string.Join(", ", integrityResult.MismatchedFilePaths.Take(5))}");
                            }
                        }

                        logger.Error($"FastInstall: Integrity check failed for '{job.Game.Name}'. Missing: {integrityResult.MissingFiles}, Mismatched: {integrityResult.MismatchedFiles}");

                        // Show error notification
                        playniteApi.Notifications.Add(new NotificationMessage(
                            $"FastInstall_IntegrityError_{job.Game.Id}",
                            $"Verifica integrità fallita per '{job.Game.Name}'. Alcuni file potrebbero essere corrotti.",
                            NotificationType.Error));

                        // Show error in progress window
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.ShowError($"Verifica integrità fallita: {integrityResult.MissingFiles} file mancanti, {integrityResult.MismatchedFiles} file con problemi");
                        });
                    }
                    else
                    {
                        logger.Info($"FastInstall: Integrity check passed for '{job.Game.Name}'. Verified {integrityResult.VerifiedFiles}/{integrityResult.TotalFiles} files");
                    }

                    job.Status = InstallationStatus.Completed;
                    
                    // Update UI
                    playniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (integrityResult.IsValid)
                        {
                            job.ProgressWindow?.ShowCompleted();
                        }
                        
                        // Invoke the completion callback
                        job.OnInstalled?.Invoke(new GameInstalledEventArgs(new GameInstallationData
                        {
                            InstallDirectory = job.DestinationPath
                        }));
                    });

                    // Show notification in Italian
                    var notificationMessage = integrityResult.IsValid
                        ? $"Installazione del gioco {job.Game.Name} completata"
                        : $"Installazione del gioco {job.Game.Name} completata con errori di integrità";

                    playniteApi.Notifications.Add(new NotificationMessage(
                        $"FastInstall_Complete_{job.Game.Id}",
                        notificationMessage,
                        integrityResult.IsValid ? NotificationType.Info : NotificationType.Error));

                    logger.Info($"FastInstall: Successfully installed '{job.Game.Name}' (Integrity: {(integrityResult.IsValid ? "OK" : "FAILED")})");

                    // Close the progress window automatically after a short delay (2 seconds)
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.Close();
                        });
                    });
                }
            }
            catch (OperationCanceledException)
            {
                job.Status = InstallationStatus.Cancelled;
                
                // Only cleanup if destination exists (copy might have started)
                bool needsCleanup = Directory.Exists(job.DestinationPath);
                if (needsCleanup)
                {
                    CleanupPartialCopy(job.DestinationPath);
                }
                
                playniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    job.ProgressWindow?.ShowCancelled(showCleaningUp: needsCleanup);
                    job.ProgressWindow?.AllowClose();
                });

                logger.Info($"FastInstall: Installation of '{job.Game.Name}' was cancelled");

                // Close the progress window automatically after a short delay (2 seconds)
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    playniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        job.ProgressWindow?.Close();
                    });
                });

                // Notify controller that installation was cancelled
                job.OnCancelled?.Invoke();
            }
            catch (Exception ex)
            {
                job.Status = InstallationStatus.Failed;
                CleanupPartialCopy(job.DestinationPath);
                
                var errorMessage = GetFriendlyErrorMessage(ex);
                
                playniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    job.ProgressWindow?.ShowError(errorMessage);
                    job.ProgressWindow?.AllowClose();
                });

                // Show notification
                playniteApi.Notifications.Add(new NotificationMessage(
                    $"FastInstall_Error_{job.Game.Id}",
                    $"Failed to install '{job.Game.Name}': {errorMessage}",
                    NotificationType.Error));

                logger.Error(ex, $"FastInstall: Error installing '{job.Game.Name}'");
            }
            finally
            {
                // Installation job finished (completed / cancelled / failed) -> remove from active installations
                activeInstallations.TryRemove(job.Game.Id, out _);
            }
        }

        /// <summary>
        /// Cancels an ongoing installation or removes it from queue
        /// </summary>
        public void CancelInstallation(Guid gameId)
        {
            if (activeInstallations.TryGetValue(gameId, out var job))
            {
                job.CancellationTokenSource?.Cancel();
                
                // If job is still pending (in queue), handle it immediately
                if (job.Status == InstallationStatus.Pending)
                {
                    // Remove from active installations immediately
                    activeInstallations.TryRemove(gameId, out _);
                    
                    // Update UI immediately
                    playniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = "Installation cancelled";
                            job.ProgressWindow.StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                            job.ProgressWindow.CancelButton.Content = "Close";
                            job.ProgressWindow.CancelButton.Background = System.Windows.Media.Brushes.Gray;
                            job.ProgressWindow.AllowClose();
                        }
                    });
                    
                    // Notify controller immediately
                    job.OnCancelled?.Invoke();
                    
                    // Close window after delay
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.Close();
                        });
                    });
                }
                
                logger.Info($"FastInstall: Cancellation requested for '{job.Game.Name}' (Status: {job.Status})");
            }
        }

        private void CleanupPartialCopy(string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath) || !Directory.Exists(destinationPath))
            {
                return;
            }

            // Run cleanup in background to avoid blocking
            Task.Run(() =>
            {
                try
                {
                    logger.Info($"FastInstall: Starting cleanup of partial copy at '{destinationPath}'");
                    
                    // Show notification that cleanup is starting
                    playniteApi.Notifications.Add(new NotificationMessage(
                        $"FastInstall_Cleanup_{destinationPath.GetHashCode()}",
                        "FastInstall: Cleaning up cancelled installation...",
                        NotificationType.Info));

                    // Use a more efficient deletion method for large directories
                    DeleteDirectoryRecursive(destinationPath);

                    logger.Info($"FastInstall: Successfully cleaned up partial copy at '{destinationPath}'");
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, $"FastInstall: Could not clean up partial copy at '{destinationPath}'");
                    playniteApi.Notifications.Add(new NotificationMessage(
                        $"FastInstall_CleanupError_{destinationPath.GetHashCode()}",
                        $"FastInstall: Could not clean up '{Path.GetFileName(destinationPath)}'. You may need to delete it manually.",
                        NotificationType.Error));
                }
            });
        }

        /// <summary>
        /// Recursively deletes a directory more efficiently than Directory.Delete for large folders
        /// </summary>
        private void DeleteDirectoryRecursive(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                // Delete files first (faster than deleting directory tree)
                var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal); // Remove read-only if needed
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore individual file errors, continue with others
                    }
                }

                // Delete directories (bottom-up)
                var dirs = Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length); // Delete deepest first
                
                foreach (var dir in dirs)
                {
                    try
                    {
                        Directory.Delete(dir, false);
                    }
                    catch
                    {
                        // Ignore individual directory errors
                    }
                }

                // Finally delete the root directory
                Directory.Delete(path, false);
            }
            catch
            {
                // Fallback to standard delete if optimized method fails
                try
                {
                    Directory.Delete(path, true);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Failed to delete directory: {ex.Message}", ex);
                }
            }
        }

        private string GetFriendlyErrorMessage(Exception ex)
        {
            if (ex is IOException ioEx)
            {
                if (ioEx.Message.Contains("not enough space") || ioEx.HResult == -2147024784)
                {
                    return "Not enough disk space";
                }
                return $"IO Error: {ioEx.Message}";
            }
            
            if (ex is UnauthorizedAccessException)
            {
                return "Permission denied - check folder permissions";
            }
            
            if (ex is DirectoryNotFoundException)
            {
                return "Source folder not found";
            }

            return ex.Message;
        }

        /// <summary>
        /// Gets the number of active installations (including queued)
        /// </summary>
        public int ActiveCount => activeInstallations.Count;

        /// <summary>
        /// Gets the number of jobs in the queue (excluding the one currently being processed)
        /// </summary>
        public int QueueCount => installQueue.Count;

        /// <summary>
        /// Gets information about the installation queue
        /// </summary>
        public QueueInfo GetQueueInfo()
        {
            return new QueueInfo
            {
                TotalActive = activeInstallations.Count,
                Queued = installQueue.Count,
                CurrentlyInstalling = activeInstallations.Values
                    .Where(j => j.Status == InstallationStatus.InProgress)
                    .Select(j => j.Game.Name)
                    .FirstOrDefault(),
                QueuedGames = installQueue
                    .Select(j => j.Game.Name)
                    .ToList()
            };
        }
    }

    /// <summary>
    /// Information about the installation queue
    /// </summary>
    public class QueueInfo
    {
        public int TotalActive { get; set; }
        public int Queued { get; set; }
        public string CurrentlyInstalling { get; set; }
        public List<string> QueuedGames { get; set; } = new List<string>();
    }
}
