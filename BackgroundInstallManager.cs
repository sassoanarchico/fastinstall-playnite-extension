using System;
using System.Collections.Concurrent;
using System.IO;
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
            Action<GameInstalledEventArgs> onInstalled)
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
                        playniteApi.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.ShowCancelled();
                            job.ProgressWindow?.AllowClose();
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
                    CleanupPartialCopy(job.DestinationPath);
                    
                    playniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        job.ProgressWindow?.ShowCancelled();
                        job.ProgressWindow?.AllowClose();
                    });

                    logger.Info($"FastInstall: Installation of '{job.Game.Name}' was cancelled");
                }
                else if (result)
                {
                    job.Status = InstallationStatus.Completed;
                    
                    // Update UI
                    playniteApi.MainView.UIDispatcher.Invoke(() =>
                    {
                        job.ProgressWindow?.ShowCompleted();
                        
                        // Invoke the completion callback
                        job.OnInstalled?.Invoke(new GameInstalledEventArgs(new GameInstallationData
                        {
                            InstallDirectory = job.DestinationPath
                        }));
                    });

                    // Show notification
                    playniteApi.Notifications.Add(new NotificationMessage(
                        $"FastInstall_Complete_{job.Game.Id}",
                        $"'{job.Game.Name}' has been installed successfully!",
                        NotificationType.Info));

                    logger.Info($"FastInstall: Successfully installed '{job.Game.Name}'");
                }
            }
            catch (OperationCanceledException)
            {
                job.Status = InstallationStatus.Cancelled;
                CleanupPartialCopy(job.DestinationPath);
                
                playniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    job.ProgressWindow?.ShowCancelled();
                    job.ProgressWindow?.AllowClose();
                });

                logger.Info($"FastInstall: Installation of '{job.Game.Name}' was cancelled");
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
                // Remove from active installations after a delay to allow window interaction
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(5));
                    activeInstallations.TryRemove(job.Game.Id, out _);
                });
            }
        }

        /// <summary>
        /// Cancels an ongoing installation
        /// </summary>
        public void CancelInstallation(Guid gameId)
        {
            if (activeInstallations.TryGetValue(gameId, out var job))
            {
                job.CancellationTokenSource?.Cancel();
                logger.Info($"FastInstall: Cancellation requested for '{job.Game.Name}'");
            }
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
        /// Gets the number of active installations
        /// </summary>
        public int ActiveCount => activeInstallations.Count;
    }
}
