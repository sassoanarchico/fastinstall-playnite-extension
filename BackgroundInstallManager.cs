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
    /// Installation priority levels
    /// </summary>
    public enum InstallationPriority
    {
        Low = 0,
        Normal = 1,
        High = 2
    }

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
        public bool IsPaused { get; set; } // Flag to distinguish pause from cancellation
        public InstallationPriority Priority { get; set; } = InstallationPriority.Normal;
    }

    public enum InstallationStatus
    {
        Pending,
        InProgress,
        Paused,
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
        private readonly object queueLock = new object(); // For priority-based queue management
        private readonly IPlayniteAPI playniteAPI;
        private SemaphoreSlim parallelInstallSemaphore;
        private int maxParallelInstalls = 1; // Default: sequential
        private readonly object settingsLock = new object();
        private Task queueProcessorTask;
        private readonly object queueProcessorLock = new object();
        private Func<string> getSevenZipPathFunc; // Function to get 7-Zip path from settings

        public static BackgroundInstallManager Instance
        {
            get
            {
                // Return null if not initialized instead of throwing exception
                // This allows UI code to safely check if instance is available
                return instance;
            }
        }

        public static void Initialize(IPlayniteAPI api, Func<string> getSevenZipPath = null)
        {
            lock (lockObj)
            {
                if (instance == null)
                {
                    instance = new BackgroundInstallManager(api, getSevenZipPath);
                }
            }
        }

        private BackgroundInstallManager(IPlayniteAPI api, Func<string> getSevenZipPath = null)
        {
            playniteAPI = api;
            activeInstallations = new ConcurrentDictionary<Guid, InstallationJob>();
            installQueue = new ConcurrentQueue<InstallationJob>();
            parallelInstallSemaphore = new SemaphoreSlim(maxParallelInstalls, maxParallelInstalls);
            getSevenZipPathFunc = getSevenZipPath;
        }

        /// <summary>
        /// Sets the function to get 7-Zip path from settings
        /// </summary>
        public void SetSevenZipPathGetter(Func<string> getter)
        {
            getSevenZipPathFunc = getter;
        }

        /// <summary>
        /// Gets the 7-Zip path from settings
        /// </summary>
        private string GetSevenZipPath()
        {
            return getSevenZipPathFunc?.Invoke() ?? string.Empty;
        }

        /// <summary>
        /// Sets the maximum number of parallel installations
        /// </summary>
        public void SetMaxParallelInstalls(int maxParallel)
        {
            if (maxParallel < 1) maxParallel = 1;
            
            lock (settingsLock)
            {
                var oldMax = maxParallelInstalls;
                maxParallelInstalls = maxParallel;
                
                // Recreate semaphore with new capacity
                // This is the safest way to ensure the semaphore matches the new limit
                var oldSemaphore = parallelInstallSemaphore;
                parallelInstallSemaphore = new SemaphoreSlim(maxParallel, maxParallel);
                
                // Dispose old semaphore after a delay to let current operations finish
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    try
                    {
                        oldSemaphore?.Dispose();
                    }
                    catch
                    {
                        // Ignore disposal errors
                    }
                });
            }
            
            logger.Info($"FastInstall: Max parallel installations set to {maxParallel}");
            
            // Restart queue processing if there are jobs waiting
            lock (queueProcessorLock)
            {
                if (installQueue.Count > 0 && (queueProcessorTask == null || queueProcessorTask.IsCompleted))
                {
                    queueProcessorTask = Task.Run(ProcessQueue);
                }
            }
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
            Action onCancelled = null,
            InstallationPriority priority = InstallationPriority.Normal)
        {
            if (IsInstalling(game.Id))
            {
                playniteAPI.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Message_AlreadyInstallingFormat"), game.Name),
                    ResourceProvider.GetString("LOCFastInstall_DialogTitle_InstallationInProgress"),
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
                Status = InstallationStatus.Pending,
                Priority = priority
            };

            activeInstallations[game.Id] = job;
            
            // Enqueue with priority consideration
            lock (queueLock)
            {
                if (priority == InstallationPriority.High)
                {
                    // For high priority, insert at the front
                    var tempList = new List<InstallationJob>();
                    while (installQueue.TryDequeue(out var existingJob))
                    {
                        tempList.Add(existingJob);
                    }
                    tempList.Insert(0, job); // Insert high priority at front
                    foreach (var j in tempList)
                    {
                        installQueue.Enqueue(j);
                    }
                }
                else
                {
                    installQueue.Enqueue(job);
                }
            }

            // Create and show progress window on UI thread
            playniteAPI.MainView.UIDispatcher.Invoke(() =>
            {
                job.ProgressWindow = new InstallationProgressWindow(
                    game.Name,
                    cts,
                    () => CancelInstallation(game.Id),
                    () => PauseInstallation(game.Id),
                    () => ResumeInstallation(game.Id),
                    startQueued: true);
                
                job.ProgressWindow.Show();
            });

            // Start processing queue if not already running
            Task.Run(ProcessQueue);

            logger.Info($"FastInstall: Queued background installation for '{game.Name}'");
        }

        /// <summary>
        /// Processes installation jobs from the queue with parallel support.
        /// </summary>
        private async Task ProcessQueue()
        {
            var activeTasks = new List<Task>();
            
            try
            {
                while (true)
                {
                    // Remove completed tasks
                    activeTasks.RemoveAll(t => t.IsCompleted);
                    
                    // Check if we can start more installations
                    int currentRunning = activeTasks.Count;
                    int maxParallel;
                    lock (settingsLock)
                    {
                        maxParallel = maxParallelInstalls;
                    }
                    
                    if (currentRunning < maxParallel)
                    {
                        // Try to get a job from queue (respecting priority)
                        InstallationJob job = null;
                        lock (queueLock)
                        {
                            // Get all jobs and sort by priority
                            var jobsList = new List<InstallationJob>();
                            while (installQueue.TryDequeue(out var tempJob))
                            {
                                jobsList.Add(tempJob);
                            }
                            
                            // Separate paused and active jobs
                            var pausedJobs = jobsList.Where(j => j.Status == InstallationStatus.Paused || j.IsPaused).ToList();
                            var activeJobs = jobsList.Except(pausedJobs).ToList();
                            
                            // Sort active jobs by priority (High first), then by StartTime (oldest first)
                            activeJobs = activeJobs
                                .OrderByDescending(j => j.Priority)
                                .ThenBy(j => j.StartTime)
                                .ToList();
                            
                            if (activeJobs.Count > 0)
                            {
                                job = activeJobs[0];
                                activeJobs.RemoveAt(0);
                            }
                            
                            // Re-enqueue remaining jobs (active first, then paused)
                            foreach (var j in activeJobs.Concat(pausedJobs))
                            {
                                installQueue.Enqueue(j);
                            }
                        }
                        
                        if (job == null)
                        {
                            // No non-paused jobs available
                            await Task.Delay(500);
                            continue;
                        }
                        
                        // Check if job was cancelled before starting
                        if (job.CancellationTokenSource.IsCancellationRequested && !job.IsPaused)
                        {
                            HandleCancelledQueuedJob(job);
                            continue;
                        }
                        
                        // If job is paused, skip it (it will be processed when resumed)
                        if (job.IsPaused || job.Status == InstallationStatus.Paused)
                        {
                            // Re-queue paused jobs
                            installQueue.Enqueue(job);
                            await Task.Delay(100);
                            continue;
                        }
                        
                        // Start installation task
                        // Capture semaphore reference to avoid issues if it's recreated
                        var semaphore = parallelInstallSemaphore;
                        var installTask = Task.Run(async () =>
                        {
                            try
                            {
                                await semaphore.WaitAsync();
                                await ExecuteInstallation(job);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        });
                        
                        activeTasks.Add(installTask);
                        logger.Info($"FastInstall: Starting queued installation for '{job.Game.Name}' ({currentRunning + 1}/{maxParallel} parallel)");
                    }
                    else if (installQueue.IsEmpty && activeTasks.Count == 0)
                    {
                        // No more work to do
                        break;
                    }
                    else
                    {
                        // Wait a bit before checking again
                        await Task.Delay(100);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FastInstall: Error in ProcessQueue");
            }
            
            // Wait for all active tasks to complete
            if (activeTasks.Count > 0)
            {
                await Task.WhenAll(activeTasks);
            }
        }

        private void HandleCancelledQueuedJob(InstallationJob job)
        {
            job.Status = InstallationStatus.Cancelled;
            
            // Remove from active installations immediately
            activeInstallations.TryRemove(job.Game.Id, out _);
            
            playniteAPI.MainView.UIDispatcher.Invoke(() =>
            {
                if (job.ProgressWindow != null)
                {
                    job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_InstallationCancelled");
                    job.ProgressWindow.StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    job.ProgressWindow.CancelButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Close");
                    job.ProgressWindow.CancelButton.Background = System.Windows.Media.Brushes.Gray;
                    job.ProgressWindow.AllowClose();
                }
            });
            
            // Notify controller that installation was cancelled
            job.OnCancelled?.Invoke();
            
            // Close the progress window automatically after a short delay (2 seconds)
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                playniteAPI.MainView.UIDispatcher.Invoke(() =>
                {
                    job.ProgressWindow?.Close();
                });
            });
            
            logger.Info($"FastInstall: Skipping queued job for '{job.Game.Name}' because it was cancelled before start.");
        }

        private async Task ExecuteInstallation(InstallationJob job)
        {
            // Clear pause flag and set status to InProgress when starting/resuming
            job.IsPaused = false;
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
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            dialogResult = MessageBox.Show(
                                string.Format(ResourceProvider.GetString("LOCFastInstall_DiskSpace_WarningMessageFormat"), job.Game.Name, requiredFormatted, availableFormatted, missingFormatted),
                                ResourceProvider.GetString("LOCFastInstall_DiskSpace_WarningTitle"),
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning);
                        });
                        return dialogResult;
                    });

                    if (userResponse == MessageBoxResult.No)
                    {
                        job.Status = InstallationStatus.Cancelled;
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.ShowError(ResourceProvider.GetString("LOCFastInstall_DiskSpace_CancelledMessage"));
                            job.ProgressWindow?.AllowClose();
                        });

                        playniteAPI.Notifications.Add(new NotificationMessage(
                            $"FastInstall_InsufficientSpace_{job.Game.Id}",
                            string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_InsufficientSpaceFormat"), job.Game.Name),
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

                // Check if source is an archive file or contains archives
                string actualSourcePath = job.SourcePath;
                string tempExtractPath = null;
                bool needsArchiveCleanup = false;

                // Check if source path is a file (archive)
                if (File.Exists(job.SourcePath) && ArchiveHelper.IsArchiveFile(job.SourcePath))
                {
                    // Source is an archive file - extract it first
                    var sevenZipPath = GetSevenZipPath();
                    if (string.IsNullOrWhiteSpace(sevenZipPath))
                    {
                        throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_SevenZipNotConfigured"));
                    }

                    // Create temporary extraction directory
                    tempExtractPath = Path.Combine(Path.GetTempPath(), "FastInstall_Extract_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempExtractPath);
                    needsArchiveCleanup = true;

                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_ExtractingArchive");
                        }
                    });

                    // Extract archive
                    var extractResult = await ArchiveHelper.ExtractArchive(
                        job.SourcePath,
                        tempExtractPath,
                        sevenZipPath,
                        (progressMessage) =>
                        {
                            playniteAPI.MainView.UIDispatcher.Invoke(() =>
                            {
                                if (job.ProgressWindow != null)
                                {
                                    job.ProgressWindow.StatusText.Text = progressMessage;
                                }
                            });
                        },
                        job.CancellationTokenSource.Token);

                    if (!extractResult)
                    {
                        throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_ArchiveExtractionFailedOrCancelled"));
                    }

                    // After extraction, use the extracted directory as source
                    // If extraction created a single subdirectory, use that
                    var extractedDirs = Directory.GetDirectories(tempExtractPath);
                    if (extractedDirs.Length == 1)
                    {
                        actualSourcePath = extractedDirs[0];
                    }
                    else
                    {
                        actualSourcePath = tempExtractPath;
                    }
                }
                else if (Directory.Exists(job.SourcePath) && ArchiveHelper.ContainsArchives(job.SourcePath))
                {
                    // Source directory contains archive files - extract the first one found
                    var archiveFile = ArchiveHelper.FindArchiveFile(job.SourcePath);
                    if (archiveFile != null)
                    {
                        var sevenZipPath = GetSevenZipPath();
                        if (string.IsNullOrWhiteSpace(sevenZipPath))
                        {
                            throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_SevenZipNotConfigured"));
                        }

                        // Create temporary extraction directory
                        tempExtractPath = Path.Combine(Path.GetTempPath(), "FastInstall_Extract_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempExtractPath);
                        needsArchiveCleanup = true;

                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            if (job.ProgressWindow != null)
                            {
                                job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_ExtractingArchive");
                            }
                        });

                        // Extract archive
                        var extractResult = await ArchiveHelper.ExtractArchive(
                            archiveFile,
                            tempExtractPath,
                            sevenZipPath,
                            (progressMessage) =>
                            {
                                playniteAPI.MainView.UIDispatcher.Invoke(() =>
                                {
                                    if (job.ProgressWindow != null)
                                    {
                                        job.ProgressWindow.StatusText.Text = progressMessage;
                                    }
                                });
                            },
                            job.CancellationTokenSource.Token);

                        if (!extractResult)
                        {
                            throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_ArchiveExtractionFailedOrCancelled"));
                        }

                        // After extraction, use the extracted directory as source
                        var extractedDirs = Directory.GetDirectories(tempExtractPath);
                        if (extractedDirs.Length == 1)
                        {
                            actualSourcePath = extractedDirs[0];
                        }
                        else
                        {
                            actualSourcePath = tempExtractPath;
                        }
                    }
                }

                // Ensure destination parent directory exists
                var destParent = Path.GetDirectoryName(job.DestinationPath);
                if (!Directory.Exists(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }

                playniteAPI.MainView.UIDispatcher.Invoke(() =>
                {
                    if (job.ProgressWindow != null)
                    {
                        job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Progress_Status_Copying");
                    }
                });

                // Copy with progress
                var result = await FileCopyHelper.CopyDirectoryWithProgress(
                    actualSourcePath,
                    job.DestinationPath,
                    (progress) =>
                    {
                        // Update UI on dispatcher thread
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.UpdateProgress(progress);
                        });
                    },
                    job.CancellationTokenSource.Token);

                // Cleanup temporary extraction directory if created
                if (needsArchiveCleanup && !string.IsNullOrWhiteSpace(tempExtractPath) && Directory.Exists(tempExtractPath))
                {
                    try
                    {
                        Directory.Delete(tempExtractPath, true);
                        logger.Info($"FastInstall: Cleaned up temporary extraction directory: {tempExtractPath}");
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, $"FastInstall: Failed to cleanup temporary extraction directory: {tempExtractPath}");
                    }
                }

                if (job.CancellationTokenSource.Token.IsCancellationRequested)
                {
                    // Check if this is a pause or a cancellation
                    if (job.IsPaused)
                    {
                        // This is a pause, not a cancellation
                        job.Status = InstallationStatus.Paused;
                        
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            if (job.ProgressWindow != null)
                            {
                                job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_InstallationPaused");
                                job.ProgressWindow.StatusText.Foreground = System.Windows.Media.Brushes.Yellow;
                            }
                        });

                        logger.Info($"FastInstall: Installation of '{job.Game.Name}' was paused");
                        
                        // Don't cleanup files, don't close window, don't notify cancellation
                        // Just return and wait for resume
                        return;
                    }
                    else
                    {
                        // This is a real cancellation
                        job.Status = InstallationStatus.Cancelled;
                        
                        // Only cleanup if we actually started copying (destination might exist)
                        bool needsCleanup = Directory.Exists(job.DestinationPath);
                        if (needsCleanup)
                        {
                            CleanupPartialCopy(job.DestinationPath);
                        }
                        
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.ShowCancelled(showCleaningUp: needsCleanup);
                            job.ProgressWindow?.AllowClose();
                        });

                        logger.Info($"FastInstall: Installation of '{job.Game.Name}' was cancelled");

                        // Close the progress window automatically after a short delay (2 seconds)
                        _ = Task.Delay(2000).ContinueWith(_ =>
                        {
                            playniteAPI.MainView.UIDispatcher.Invoke(() =>
                            {
                                job.ProgressWindow?.Close();
                            });
                        });

                        // Notify controller that installation was cancelled
                        job.OnCancelled?.Invoke();
                    }
                }
                else if (result)
                {
                    // Perform integrity check after copy
                    logger.Info($"FastInstall: Starting integrity check for '{job.Game.Name}'...");
                    
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_IntegrityCheck");
                        }
                    });

                    var integrityResult = await Task.Run(() =>
                    {
                        return FileCopyHelper.VerifyCopyIntegrity(
                            job.SourcePath,
                            job.DestinationPath,
                            (message) =>
                            {
                                playniteAPI.MainView.UIDispatcher.Invoke(() =>
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
                        errorDetails.AppendLine(string.Format(ResourceProvider.GetString("LOCFastInstall_Integrity_Error_HeaderFormat"), job.Game.Name));
                        
                        if (integrityResult.MissingFiles > 0)
                        {
                            errorDetails.AppendLine(string.Format(ResourceProvider.GetString("LOCFastInstall_Integrity_Error_MissingFilesFormat"), integrityResult.MissingFiles));
                            if (integrityResult.MissingFilePaths.Count > 0)
                            {
                                errorDetails.AppendLine(string.Format(
                                    ResourceProvider.GetString("LOCFastInstall_Integrity_Error_MissingFilesSampleFormat"),
                                    string.Join(", ", integrityResult.MissingFilePaths.Take(5))));
                            }
                        }
                        
                        if (integrityResult.MismatchedFiles > 0)
                        {
                            errorDetails.AppendLine(string.Format(ResourceProvider.GetString("LOCFastInstall_Integrity_Error_MismatchedFilesFormat"), integrityResult.MismatchedFiles));
                            if (integrityResult.MismatchedFilePaths.Count > 0)
                            {
                                errorDetails.AppendLine(string.Format(
                                    ResourceProvider.GetString("LOCFastInstall_Integrity_Error_MismatchedFilesSampleFormat"),
                                    string.Join(", ", integrityResult.MismatchedFilePaths.Take(5))));
                            }
                        }

                        logger.Error($"FastInstall: Integrity check failed for '{job.Game.Name}'. Missing: {integrityResult.MissingFiles}, Mismatched: {integrityResult.MismatchedFiles}");

                        // Show error notification
                        playniteAPI.Notifications.Add(new NotificationMessage(
                            $"FastInstall_IntegrityError_{job.Game.Id}",
                            string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_IntegrityFailedFormat"), job.Game.Name),
                            NotificationType.Error));

                        // Show error in progress window
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            var shortMsg = string.Format(
                                ResourceProvider.GetString("LOCFastInstall_Integrity_Error_ShortFormat"),
                                integrityResult.MissingFiles,
                                integrityResult.MismatchedFiles);
                            job.ProgressWindow?.ShowError(shortMsg);
                        });
                    }
                    else
                    {
                        logger.Info($"FastInstall: Integrity check passed for '{job.Game.Name}'. Verified {integrityResult.VerifiedFiles}/{integrityResult.TotalFiles} files");
                    }

                    job.Status = InstallationStatus.Completed;
                    
                    // Update UI
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
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
                        ? string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_InstallCompletedFormat"), job.Game.Name)
                        : string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_InstallCompletedWithIntegrityErrorsFormat"), job.Game.Name);

                    playniteAPI.Notifications.Add(new NotificationMessage(
                        $"FastInstall_Complete_{job.Game.Id}",
                        notificationMessage,
                        integrityResult.IsValid ? NotificationType.Info : NotificationType.Error));

                    logger.Info($"FastInstall: Successfully installed '{job.Game.Name}' (Integrity: {(integrityResult.IsValid ? "OK" : "FAILED")})");

                    // Close the progress window automatically after a short delay (2 seconds)
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.Close();
                        });
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Check if this is a pause or a cancellation
                if (job.IsPaused)
                {
                    // This is a pause, not a cancellation
                    job.Status = InstallationStatus.Paused;
                    
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_InstallationPaused");
                            job.ProgressWindow.StatusText.Foreground = System.Windows.Media.Brushes.Yellow;
                            job.ProgressWindow.UpdatePauseState(true);
                        }
                    });

                    logger.Info($"FastInstall: Installation of '{job.Game.Name}' was paused");
                    
                    // Don't cleanup files, don't close window, don't notify cancellation
                    // Don't remove from active installations - keep it so it can be resumed
                    // Just return and wait for resume
                    return;
                }
                else
                {
                    // This is a real cancellation
                    job.Status = InstallationStatus.Cancelled;
                    job.IsPaused = false; // Clear pause flag on cancellation
                    
                    // Only cleanup if destination exists (copy might have started)
                    bool needsCleanup = Directory.Exists(job.DestinationPath);
                    if (needsCleanup)
                    {
                        CleanupPartialCopy(job.DestinationPath);
                    }
                    
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        job.ProgressWindow?.ShowCancelled(showCleaningUp: needsCleanup);
                        job.ProgressWindow?.AllowClose();
                    });

                    logger.Info($"FastInstall: Installation of '{job.Game.Name}' was cancelled");

                    // Close the progress window automatically after a short delay (2 seconds)
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.Close();
                        });
                    });

                    // Notify controller that installation was cancelled
                    job.OnCancelled?.Invoke();
                }
            }
            catch (Exception ex)
            {
                job.Status = InstallationStatus.Failed;
                CleanupPartialCopy(job.DestinationPath);
                
                var errorMessage = GetFriendlyErrorMessage(ex);
                
                playniteAPI.MainView.UIDispatcher.Invoke(() =>
                {
                    job.ProgressWindow?.ShowError(errorMessage);
                    job.ProgressWindow?.AllowClose();
                });

                // Show notification
                playniteAPI.Notifications.Add(new NotificationMessage(
                    $"FastInstall_Error_{job.Game.Id}",
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_InstallFailedFormat"), job.Game.Name, errorMessage),
                    NotificationType.Error));

                logger.Error(ex, $"FastInstall: Error installing '{job.Game.Name}'");
            }
            finally
            {
                // Installation job finished (completed / cancelled / failed) -> remove from active installations
                // BUT: Don't remove if it's paused - we need to keep it for resume
                if (job.Status != InstallationStatus.Paused && !job.IsPaused)
                {
                    activeInstallations.TryRemove(job.Game.Id, out _);
                }
            }
        }

        /// <summary>
        /// Cancels an ongoing installation or removes it from queue
        /// </summary>
        public void CancelInstallation(Guid gameId)
        {
            if (activeInstallations.TryGetValue(gameId, out var job))
            {
                // Clear pause flag on cancellation
                job.IsPaused = false;
                job.CancellationTokenSource?.Cancel();
                
                // If job is still pending (in queue), handle it immediately
                if (job.Status == InstallationStatus.Pending)
                {
                    // Remove from active installations immediately
                    activeInstallations.TryRemove(gameId, out _);
                    
                    // Update UI immediately
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_InstallationCancelled");
                            job.ProgressWindow.StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                            job.ProgressWindow.CancelButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Close");
                            job.ProgressWindow.CancelButton.Background = System.Windows.Media.Brushes.Gray;
                            job.ProgressWindow.AllowClose();
                        }
                    });
                    
                    // Notify controller immediately
                    job.OnCancelled?.Invoke();
                    
                    // Close window after delay
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.Close();
                        });
                    });
                }
                else if (job.Status == InstallationStatus.Paused)
                {
                    // If paused, treat as cancellation and cleanup
                    job.Status = InstallationStatus.Cancelled;
                    activeInstallations.TryRemove(gameId, out _);
                    
                    // Cleanup partial files
                    bool needsCleanup = Directory.Exists(job.DestinationPath);
                    if (needsCleanup)
                    {
                        CleanupPartialCopy(job.DestinationPath);
                    }
                    
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.ShowCancelled(showCleaningUp: needsCleanup);
                            job.ProgressWindow.AllowClose();
                        }
                    });
                    
                    job.OnCancelled?.Invoke();
                    
                    // Close window after delay
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.Close();
                        });
                    });
                }
                
                logger.Info($"FastInstall: Cancellation requested for '{job.Game.Name}' (Status: {job.Status})");
            }
        }

        /// <summary>
        /// Pauses an ongoing installation
        /// </summary>
        public void PauseInstallation(Guid gameId)
        {
            if (activeInstallations.TryGetValue(gameId, out var job))
            {
                if (job.Status == InstallationStatus.InProgress)
                {
                    // Set pause flag BEFORE cancelling token
                    job.IsPaused = true;
                    job.Status = InstallationStatus.Paused;
                    job.CancellationTokenSource?.Cancel(); // This will stop the copy operation
                    
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_InstallationPaused");
                            job.ProgressWindow.StatusText.Foreground = System.Windows.Media.Brushes.Yellow;
                            job.ProgressWindow.UpdatePauseState(true);
                        }
                    });
                    
                    logger.Info($"FastInstall: Installation paused for '{job.Game.Name}' - files preserved for resume");
                }
            }
        }

        /// <summary>
        /// Resumes a paused installation
        /// </summary>
        public void ResumeInstallation(Guid gameId)
        {
            if (activeInstallations.TryGetValue(gameId, out var job))
            {
                if (job.Status == InstallationStatus.Paused || job.IsPaused)
                {
                    // Clear pause flag and create new cancellation token
                    job.IsPaused = false;
                    job.Status = InstallationStatus.Pending;
                    job.CancellationTokenSource = new CancellationTokenSource();
                    
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_ResumingInstallation");
                            job.ProgressWindow.StatusText.Foreground = System.Windows.Media.Brushes.White;
                            job.ProgressWindow.UpdatePauseState(false);
                        }
                    });
                    
                    // Re-queue the job at the front of the queue (priority)
                    installQueue.Enqueue(job);
                    
                    // Restart queue processing if needed
                    lock (queueProcessorLock)
                    {
                        if (queueProcessorTask == null || queueProcessorTask.IsCompleted)
                        {
                            queueProcessorTask = Task.Run(ProcessQueue);
                        }
                    }
                    
                    logger.Info($"FastInstall: Installation resumed for '{job.Game.Name}' - will continue from existing files");
                }
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
                    playniteAPI.Notifications.Add(new NotificationMessage(
                        $"FastInstall_Cleanup_{destinationPath.GetHashCode()}",
                        ResourceProvider.GetString("LOCFastInstall_Notification_CleanupStarting"),
                        NotificationType.Info));

                    // Use a more efficient deletion method for large directories
                    DeleteDirectoryRecursive(destinationPath);

                    logger.Info($"FastInstall: Successfully cleaned up partial copy at '{destinationPath}'");
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, $"FastInstall: Could not clean up partial copy at '{destinationPath}'");
                    playniteAPI.Notifications.Add(new NotificationMessage(
                        $"FastInstall_CleanupError_{destinationPath.GetHashCode()}",
                        string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_CleanupFailedFormat"), Path.GetFileName(destinationPath)),
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
                    return ResourceProvider.GetString("LOCFastInstall_Error_NotEnoughDiskSpace");
                }
                return string.Format(ResourceProvider.GetString("LOCFastInstall_Error_IoErrorFormat"), ioEx.Message);
            }
            
            if (ex is UnauthorizedAccessException)
            {
                return ResourceProvider.GetString("LOCFastInstall_Error_PermissionDenied");
            }
            
            if (ex is DirectoryNotFoundException)
            {
                return ResourceProvider.GetString("LOCFastInstall_Error_SourceFolderNotFound");
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
        public int QueueCount
        {
            get
            {
                lock (queueLock)
                {
                    return installQueue.Count;
                }
            }
        }

        /// <summary>
        /// Gets all active installation jobs (including queued and paused)
        /// </summary>
        public List<InstallationJob> GetAllJobs()
        {
            var allJobs = new List<InstallationJob>();
            allJobs.AddRange(activeInstallations.Values);
            lock (queueLock)
            {
                allJobs.AddRange(installQueue);
            }
            // Remove duplicates based on Game.Id
            return allJobs.GroupBy(j => j.Game.Id).Select(g => g.First()).ToList();
        }

        /// <summary>
        /// Changes the priority of an installation job
        /// </summary>
        public void SetJobPriority(Guid gameId, InstallationPriority priority)
        {
            if (activeInstallations.TryGetValue(gameId, out var job))
            {
                job.Priority = priority;
                logger.Info($"FastInstall: Changed priority of '{job.Game.Name}' to {priority}");
            }
        }

        /// <summary>
        /// Gets information about the installation queue
        /// </summary>
        public QueueInfo GetQueueInfo()
        {
            lock (queueLock)
            {
                return new QueueInfo
                {
                    TotalActive = activeInstallations.Count,
                    Queued = installQueue.Count(j => j.Status != InstallationStatus.Paused),
                    Paused = activeInstallations.Values.Count(j => j.Status == InstallationStatus.Paused) + 
                             installQueue.Count(j => j.Status == InstallationStatus.Paused),
                    CurrentlyInstalling = activeInstallations.Values
                        .Where(j => j.Status == InstallationStatus.InProgress)
                        .Select(j => j.Game.Name)
                        .ToList(),
                    QueuedGames = installQueue
                        .Where(j => j.Status != InstallationStatus.Paused)
                        .Select(j => j.Game.Name)
                        .ToList(),
                    PausedGames = activeInstallations.Values
                        .Where(j => j.Status == InstallationStatus.Paused)
                        .Select(j => j.Game.Name)
                        .Concat(installQueue.Where(j => j.Status == InstallationStatus.Paused).Select(j => j.Game.Name))
                        .ToList()
                };
            }
        }
    }

    /// <summary>
    /// Information about the installation queue
    /// </summary>
    public class QueueInfo
    {
        public int TotalActive { get; set; }
        public int Queued { get; set; }
        public int Paused { get; set; }
        public List<string> CurrentlyInstalling { get; set; } = new List<string>();
        public List<string> QueuedGames { get; set; } = new List<string>();
        public List<string> PausedGames { get; set; } = new List<string>();
    }
}
