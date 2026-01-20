using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace FastInstall
{
    /// <summary>
    /// Represents a cloud download job
    /// </summary>
    public class CloudDownloadJob
    {
        public Guid JobId { get; set; } = Guid.NewGuid();
        public Game Game { get; set; }
        public string FileId { get; set; }
        public string FileName { get; set; }
        public string DestinationPath { get; set; }
        public string TempDownloadPath { get; set; }
        public CloudProvider Provider { get; set; }
        public InstallationStatus Status { get; set; } = InstallationStatus.Pending;
        public InstallationPriority Priority { get; set; } = InstallationPriority.Normal;
        public CancellationTokenSource CancellationTokenSource { get; set; }
        public InstallationProgressWindow ProgressWindow { get; set; }
        public DateTime StartTime { get; set; } = DateTime.Now;
        public bool IsPaused { get; set; }
        public bool IsArchive { get; set; }
        public long TotalBytes { get; set; }
        public long BytesDownloaded { get; set; }

        // Callbacks
        public Action<GameInstalledEventArgs> OnInstalled { get; set; }
        public Action OnCancelled { get; set; }
    }

    /// <summary>
    /// Manages cloud downloads with queue support and progress tracking
    /// </summary>
    public class CloudDownloadManager
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static CloudDownloadManager instance;
        private static readonly object instanceLock = new object();

        private readonly IPlayniteAPI playniteAPI;
        private readonly ConcurrentQueue<CloudDownloadJob> downloadQueue = new ConcurrentQueue<CloudDownloadJob>();
        private readonly ConcurrentDictionary<Guid, CloudDownloadJob> activeDownloads = new ConcurrentDictionary<Guid, CloudDownloadJob>();
        private readonly object queueLock = new object();
        private readonly SemaphoreSlim downloadSemaphore;
        private bool isProcessingQueue = false;

        private readonly Dictionary<CloudProvider, ICloudStorageProvider> providers = new Dictionary<CloudProvider, ICloudStorageProvider>();
        private Func<string> getSevenZipPathFunc;

        public static CloudDownloadManager Instance
        {
            get
            {
                lock (instanceLock)
                {
                    return instance;
                }
            }
        }

        public static void Initialize(IPlayniteAPI api, int maxParallelDownloads = 2, Func<string> sevenZipPathGetter = null)
        {
            lock (instanceLock)
            {
                if (instance == null)
                {
                    instance = new CloudDownloadManager(api, maxParallelDownloads, sevenZipPathGetter);
                    logger.Info("CloudDownloadManager: Initialized");
                }
            }
        }

        private CloudDownloadManager(IPlayniteAPI api, int maxParallelDownloads, Func<string> sevenZipPathGetter)
        {
            playniteAPI = api;
            downloadSemaphore = new SemaphoreSlim(maxParallelDownloads, maxParallelDownloads);
            getSevenZipPathFunc = sevenZipPathGetter;

            // Register default providers
            RegisterProvider(new GoogleDriveProvider());
        }

        public void RegisterProvider(ICloudStorageProvider provider)
        {
            providers[provider.Provider] = provider;
            logger.Info($"CloudDownloadManager: Registered provider {provider.DisplayName}");
        }

        public ICloudStorageProvider GetProvider(CloudProvider providerType)
        {
            if (providers.TryGetValue(providerType, out var provider))
            {
                return provider;
            }
            return null;
        }

        public void SetProviderApiKey(CloudProvider providerType, string apiKey)
        {
            if (providers.TryGetValue(providerType, out var provider))
            {
                provider.SetApiKey(apiKey);
            }
        }

        public void SetSevenZipPathGetter(Func<string> getter)
        {
            getSevenZipPathFunc = getter;
        }

        public void SetMaxParallelDownloads(int max)
        {
            // Note: SemaphoreSlim doesn't support changing max after creation
            // This would require recreating the semaphore
            logger.Info($"CloudDownloadManager: Max parallel downloads set to {max}");
        }

        /// <summary>
        /// Start a cloud download
        /// </summary>
        public CloudDownloadJob StartDownload(
            Game game,
            string fileId,
            string fileName,
            string destinationPath,
            CloudProvider provider,
            bool isArchive,
            Action<GameInstalledEventArgs> onInstalled = null,
            Action onCancelled = null,
            InstallationPriority priority = InstallationPriority.Normal)
        {
            var cts = new CancellationTokenSource();

            // Create temp download path
            var tempDir = Path.Combine(Path.GetTempPath(), "FastInstall", "CloudDownloads");
            if (!Directory.Exists(tempDir))
            {
                Directory.CreateDirectory(tempDir);
            }
            var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}_{fileName}");

            var job = new CloudDownloadJob
            {
                Game = game,
                FileId = fileId,
                FileName = fileName,
                DestinationPath = destinationPath,
                TempDownloadPath = tempPath,
                Provider = provider,
                IsArchive = isArchive,
                Priority = priority,
                CancellationTokenSource = cts,
                OnInstalled = onInstalled,
                OnCancelled = onCancelled,
                Status = InstallationStatus.Pending
            };

            // Create progress window
            playniteAPI.MainView.UIDispatcher.Invoke(() =>
            {
                // Game name already has [Cloud] at the end, so use it as is
                job.ProgressWindow = new InstallationProgressWindow(
                    game.Name,
                    cts,
                    () => CancelDownload(job.JobId),
                    () => PauseDownload(job.JobId),
                    () => ResumeDownload(job.JobId),
                    startQueued: true);
                job.ProgressWindow.Show();
            });

            // Add to queue and active downloads
            lock (queueLock)
            {
                activeDownloads[job.JobId] = job;
                downloadQueue.Enqueue(job);
            }

            logger.Info($"CloudDownloadManager: Queued download for '{game.Name}' from {provider}");

            // Start processing queue
            ProcessQueue();

            return job;
        }

        private async void ProcessQueue()
        {
            if (isProcessingQueue) return;

            isProcessingQueue = true;

            try
            {
                while (true)
                {
                    CloudDownloadJob job = null;

                    lock (queueLock)
                    {
                        // Get all pending jobs
                        var pendingJobs = new List<CloudDownloadJob>();
                        while (downloadQueue.TryDequeue(out var queuedJob))
                        {
                            if (queuedJob.Status == InstallationStatus.Pending && !queuedJob.IsPaused)
                            {
                                pendingJobs.Add(queuedJob);
                            }
                            else if (queuedJob.Status == InstallationStatus.Paused || queuedJob.IsPaused)
                            {
                                // Re-queue paused jobs
                                downloadQueue.Enqueue(queuedJob);
                            }
                        }

                        if (pendingJobs.Count == 0) break;

                        // Sort by priority
                        pendingJobs = pendingJobs
                            .OrderByDescending(j => (int)j.Priority)
                            .ThenBy(j => j.StartTime)
                            .ToList();

                        job = pendingJobs.First();

                        // Re-queue the rest
                        foreach (var remaining in pendingJobs.Skip(1))
                        {
                            downloadQueue.Enqueue(remaining);
                        }
                    }

                    if (job == null) break;

                    // Wait for semaphore
                    await downloadSemaphore.WaitAsync();

                    try
                    {
                        await ExecuteDownload(job);
                    }
                    finally
                    {
                        downloadSemaphore.Release();
                    }
                }
            }
            finally
            {
                isProcessingQueue = false;
            }
        }

        private async Task ExecuteDownload(CloudDownloadJob job)
        {
            job.Status = InstallationStatus.InProgress;
            job.IsPaused = false;

            try
            {
                var provider = GetProvider(job.Provider);
                if (provider == null)
                {
                    throw new Exception(string.Format(ResourceProvider.GetString("LOCFastInstall_Error_ProviderNotFoundFormat"), job.Provider));
                }

                logger.Info($"CloudDownloadManager: Starting download for '{job.Game.Name}'");
                logger.Info($"CloudDownloadManager: FileId = {job.FileId}");
                logger.Info($"CloudDownloadManager: TempPath = {job.TempDownloadPath}");
                logger.Info($"CloudDownloadManager: Destination = {job.DestinationPath}");

                // Update progress window
                playniteAPI.MainView.UIDispatcher.Invoke(() =>
                {
                    if (job.ProgressWindow != null)
                    {
                        job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_ConnectingGoogleDrive");
                    }
                });

                // Download file
                logger.Info($"CloudDownloadManager: Calling DownloadFileAsync...");
                var success = await provider.DownloadFileAsync(
                    job.FileId,
                    job.TempDownloadPath,
                    (progress) =>
                    {
                        job.BytesDownloaded = progress.BytesDownloaded;
                        job.TotalBytes = progress.TotalBytes;

                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            if (job.ProgressWindow != null)
                            {
                                job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_DownloadingGoogleDrive");
                                job.ProgressWindow.UpdateProgress(new CopyProgressInfo
                                {
                                    CopiedBytes = progress.BytesDownloaded,
                                    TotalBytes = progress.TotalBytes,
                                    CurrentFile = progress.CurrentFile ?? job.FileName,
                                    SpeedBytesPerSecond = progress.SpeedBytesPerSecond,
                                    Elapsed = progress.Elapsed,
                                    FilesCopied = 1,
                                    TotalFiles = 1
                                });
                            }
                        });
                    },
                    job.CancellationTokenSource.Token);

                logger.Info($"CloudDownloadManager: DownloadFileAsync returned: {success}");

                if (!success)
                {
                    throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_DownloadReturnedFailure"));
                }
                
                // Verify file was downloaded
                if (!File.Exists(job.TempDownloadPath))
                {
                    throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_DownloadedFileNotFound"));
                }
                
                var downloadedFile = new FileInfo(job.TempDownloadPath);
                logger.Info($"CloudDownloadManager: Downloaded file size: {downloadedFile.Length} bytes");
                
                if (downloadedFile.Length == 0)
                {
                    throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_DownloadedFileEmpty"));
                }

                // Handle archive extraction if needed
                string finalSourcePath = job.TempDownloadPath;
                string tempExtractPath = null;

                if (job.IsArchive && ArchiveHelper.IsArchiveFile(job.TempDownloadPath))
                {
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_ExtractingArchive");
                        }
                    });

                    var sevenZipPath = getSevenZipPathFunc?.Invoke();
                    if (string.IsNullOrWhiteSpace(sevenZipPath) || !File.Exists(sevenZipPath))
                    {
                        throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_SevenZipNotConfigured"));
                    }

                    tempExtractPath = Path.Combine(Path.GetTempPath(), "FastInstall", "Extract", Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempExtractPath);

                    var extractSuccess = await ArchiveHelper.ExtractArchive(
                        job.TempDownloadPath,
                        tempExtractPath,
                        sevenZipPath,
                        (msg) =>
                        {
                            playniteAPI.MainView.UIDispatcher.Invoke(() =>
                            {
                                if (job.ProgressWindow != null)
                                {
                                    job.ProgressWindow.StatusText.Text = msg;
                                }
                            });
                        },
                        job.CancellationTokenSource.Token);

                    if (!extractSuccess)
                    {
                        throw new Exception(ResourceProvider.GetString("LOCFastInstall_Error_ArchiveExtractionFailed"));
                    }

                    // Check if extraction created a single subfolder
                    var extractedDirs = Directory.GetDirectories(tempExtractPath);
                    if (extractedDirs.Length == 1 && Directory.GetFiles(tempExtractPath).Length == 0)
                    {
                        finalSourcePath = extractedDirs[0];
                    }
                    else
                    {
                        finalSourcePath = tempExtractPath;
                    }
                }

                // Copy to final destination
                playniteAPI.MainView.UIDispatcher.Invoke(() =>
                {
                    if (job.ProgressWindow != null)
                    {
                        job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_CopyingToDestination");
                    }
                });

                // Determine final destination
                string finalDestination;
                if (job.IsArchive || Directory.Exists(finalSourcePath))
                {
                    // For archives/folders, copy to destination folder
                    finalDestination = job.DestinationPath;
                    if (!Directory.Exists(finalDestination))
                    {
                        Directory.CreateDirectory(finalDestination);
                    }

                    // Copy directory contents
                    await FileCopyHelper.CopyDirectoryWithProgress(
                        finalSourcePath,
                        finalDestination,
                        (progress) =>
                        {
                            playniteAPI.MainView.UIDispatcher.Invoke(() =>
                            {
                                job.ProgressWindow?.UpdateProgress(progress);
                            });
                        },
                        job.CancellationTokenSource.Token);
                }
                else
                {
                    // For single files, copy directly
                    finalDestination = Path.Combine(job.DestinationPath, job.FileName);
                    var destDir = Path.GetDirectoryName(finalDestination);
                    if (!Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }
                    File.Copy(job.TempDownloadPath, finalDestination, true);
                }

                // Cleanup temp files
                try
                {
                    if (File.Exists(job.TempDownloadPath))
                        File.Delete(job.TempDownloadPath);
                    if (tempExtractPath != null && Directory.Exists(tempExtractPath))
                        Directory.Delete(tempExtractPath, true);
                }
                catch { }

                // Mark as completed
                job.Status = InstallationStatus.Completed;

                playniteAPI.MainView.UIDispatcher.Invoke(() =>
                {
                    job.ProgressWindow?.ShowCompleted();
                    job.OnInstalled?.Invoke(new GameInstalledEventArgs(new GameInstallationData
                    {
                        InstallDirectory = job.DestinationPath
                    }));
                });

                // Show notification
                playniteAPI.Notifications.Add(new NotificationMessage(
                    $"FastInstall_CloudComplete_{job.JobId}",
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_CloudCompletedFormat"), job.Game.Name),
                    NotificationType.Info));

                logger.Info($"CloudDownloadManager: Completed download for '{job.Game.Name}'");

                // Auto-close window
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        job.ProgressWindow?.Close();
                    });
                });
            }
            catch (OperationCanceledException)
            {
                if (job.IsPaused)
                {
                    job.Status = InstallationStatus.Paused;
                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        if (job.ProgressWindow != null)
                        {
                            job.ProgressWindow.StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Status_DownloadPaused");
                            job.ProgressWindow.UpdatePauseState(true);
                        }
                    });
                    logger.Info($"CloudDownloadManager: Paused download for '{job.Game.Name}'");
                }
                else
                {
                    job.Status = InstallationStatus.Cancelled;
                    CleanupTempFiles(job);

                    playniteAPI.MainView.UIDispatcher.Invoke(() =>
                    {
                        job.ProgressWindow?.ShowCancelled(false);
                        job.ProgressWindow?.AllowClose();
                        job.OnCancelled?.Invoke();
                    });

                    logger.Info($"CloudDownloadManager: Cancelled download for '{job.Game.Name}'");

                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        playniteAPI.MainView.UIDispatcher.Invoke(() =>
                        {
                            job.ProgressWindow?.Close();
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                job.Status = InstallationStatus.Failed;
                CleanupTempFiles(job);

                var errorMsg = ex.Message;
                logger.Error(ex, $"CloudDownloadManager: Error downloading '{job.Game.Name}'");

                playniteAPI.MainView.UIDispatcher.Invoke(() =>
                {
                    job.ProgressWindow?.ShowError(errorMsg);
                    job.ProgressWindow?.AllowClose();
                });

                playniteAPI.Notifications.Add(new NotificationMessage(
                    $"FastInstall_CloudError_{job.JobId}",
                    string.Format(ResourceProvider.GetString("LOCFastInstall_Notification_CloudFailedFormat"), job.Game.Name, errorMsg),
                    NotificationType.Error));
            }
            finally
            {
                if (job.Status != InstallationStatus.Paused)
                {
                    activeDownloads.TryRemove(job.JobId, out _);
                }
            }
        }

        private void CleanupTempFiles(CloudDownloadJob job)
        {
            try
            {
                if (File.Exists(job.TempDownloadPath))
                    File.Delete(job.TempDownloadPath);
            }
            catch { }
        }

        public void CancelDownload(Guid jobId)
        {
            if (activeDownloads.TryGetValue(jobId, out var job))
            {
                job.IsPaused = false;
                job.CancellationTokenSource?.Cancel();
            }
        }

        public void PauseDownload(Guid jobId)
        {
            if (activeDownloads.TryGetValue(jobId, out var job))
            {
                job.IsPaused = true;
                job.CancellationTokenSource?.Cancel();
            }
        }

        public void ResumeDownload(Guid jobId)
        {
            if (activeDownloads.TryGetValue(jobId, out var job))
            {
                if (job.Status == InstallationStatus.Paused)
                {
                    job.IsPaused = false;
                    job.Status = InstallationStatus.Pending;
                    job.CancellationTokenSource = new CancellationTokenSource();

                    lock (queueLock)
                    {
                        downloadQueue.Enqueue(job);
                    }

                    ProcessQueue();
                }
            }
        }

        public List<CloudDownloadJob> GetAllJobs()
        {
            return activeDownloads.Values.ToList();
        }

        public int QueueCount
        {
            get
            {
                lock (queueLock)
                {
                    return downloadQueue.Count;
                }
            }
        }
    }
}
