using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace FastInstall
{
    /// <summary>
    /// Progress information for file copy operations
    /// </summary>
    public class CopyProgressInfo
    {
        public long TotalBytes { get; set; }
        public long CopiedBytes { get; set; }
        public int PercentComplete { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan EstimatedRemaining { get; set; }
        public string CurrentFile { get; set; }
        public int FilesCopied { get; set; }
        public int TotalFiles { get; set; }

        public string SpeedFormatted => FormatSpeed(SpeedBytesPerSecond);
        public string CopiedFormatted => FormatBytes(CopiedBytes);
        public string TotalFormatted => FormatBytes(TotalBytes);
        public string ElapsedFormatted => FormatTimeSpan(Elapsed);
        public string RemainingFormatted => FormatTimeSpan(EstimatedRemaining);

        private static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int suffixIndex = 0;
            double size = bytes;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return $"{size:0.##} {suffixes[suffixIndex]}";
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 1024)
                return $"{bytesPerSecond:0.#} B/s";
            else if (bytesPerSecond < 1024 * 1024)
                return $"{bytesPerSecond / 1024:0.#} KB/s";
            else if (bytesPerSecond < 1024 * 1024 * 1024)
                return $"{bytesPerSecond / (1024 * 1024):0.##} MB/s";
            else
                return $"{bytesPerSecond / (1024 * 1024 * 1024):0.##} GB/s";
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays}d {ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            else if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            else
                return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        public override string ToString()
        {
            return $"{CopiedFormatted} / {TotalFormatted} ({PercentComplete}%) - {SpeedFormatted} - ETA: {RemainingFormatted}";
        }
    }

    /// <summary>
    /// Helper class for file copy operations with progress reporting and cancellation support
    /// </summary>
    public static class FileCopyHelper
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>
        /// Copies a directory with detailed progress reporting
        /// </summary>
        public static async Task<bool> CopyDirectoryWithProgress(
            string sourcePath,
            string destinationPath,
            Action<CopyProgressInfo> progressCallback,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var progressInfo = new CopyProgressInfo();

                    // Calculate total size first
                    logger.Info($"FastInstall: Calculating size of '{sourcePath}'...");
                    var allFiles = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
                    progressInfo.TotalFiles = allFiles.Length;
                    progressInfo.TotalBytes = 0;

                    foreach (var file in allFiles)
                    {
                        try
                        {
                            progressInfo.TotalBytes += new FileInfo(file).Length;
                        }
                        catch { }
                    }

                    logger.Info($"FastInstall: Total size: {progressInfo.TotalFormatted}, {progressInfo.TotalFiles} files");

                    // Create destination directory
                    Directory.CreateDirectory(destinationPath);

                    // Copy files with progress tracking
                    long lastUpdateBytes = 0;
                    DateTime lastUpdateTime = DateTime.Now;
                    const int updateIntervalMs = 250; // Update every 250ms

                    foreach (var sourceFile in allFiles)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return false;
                        }

                        var relativePath = sourceFile.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar);
                        var destFile = Path.Combine(destinationPath, relativePath);
                        var destDir = Path.GetDirectoryName(destFile);

                        // Ensure destination directory exists
                        if (!Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        var fileInfo = new FileInfo(sourceFile);
                        progressInfo.CurrentFile = relativePath;

                        // Copy file with buffer for large files
                        CopyFileWithProgress(sourceFile, destFile, fileInfo.Length, (bytesCopied) =>
                        {
                            progressInfo.CopiedBytes += bytesCopied;
                            progressInfo.Elapsed = stopwatch.Elapsed;

                            // Calculate speed (use moving average)
                            var timeSinceLastUpdate = (DateTime.Now - lastUpdateTime).TotalMilliseconds;
                            if (timeSinceLastUpdate >= updateIntervalMs)
                            {
                                var bytesSinceLastUpdate = progressInfo.CopiedBytes - lastUpdateBytes;
                                progressInfo.SpeedBytesPerSecond = bytesSinceLastUpdate / (timeSinceLastUpdate / 1000.0);

                                // Calculate ETA
                                if (progressInfo.SpeedBytesPerSecond > 0)
                                {
                                    var remainingBytes = progressInfo.TotalBytes - progressInfo.CopiedBytes;
                                    var remainingSeconds = remainingBytes / progressInfo.SpeedBytesPerSecond;
                                    progressInfo.EstimatedRemaining = TimeSpan.FromSeconds(remainingSeconds);
                                }

                                progressInfo.PercentComplete = (int)((progressInfo.CopiedBytes * 100) / progressInfo.TotalBytes);

                                lastUpdateBytes = progressInfo.CopiedBytes;
                                lastUpdateTime = DateTime.Now;

                                // Report progress
                                progressCallback?.Invoke(progressInfo);
                            }
                        }, cancellationToken);

                        progressInfo.FilesCopied++;
                    }

                    // Copy empty directories
                    foreach (var sourceDir in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return false;
                        }

                        var relativePath = sourceDir.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar);
                        var destDir = Path.Combine(destinationPath, relativePath);

                        if (!Directory.Exists(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }
                    }

                    stopwatch.Stop();

                    // Final progress update
                    progressInfo.PercentComplete = 100;
                    progressInfo.Elapsed = stopwatch.Elapsed;
                    progressInfo.EstimatedRemaining = TimeSpan.Zero;
                    progressInfo.SpeedBytesPerSecond = progressInfo.TotalBytes / stopwatch.Elapsed.TotalSeconds;
                    progressCallback?.Invoke(progressInfo);

                    logger.Info($"FastInstall: Copy completed in {progressInfo.ElapsedFormatted}, average speed: {progressInfo.SpeedFormatted}");

                    return true;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "FastInstall: Copy failed");
                    throw;
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Copies a single file with progress tracking
        /// </summary>
        private static void CopyFileWithProgress(
            string sourceFile,
            string destFile,
            long fileSize,
            Action<long> bytesWrittenCallback,
            CancellationToken cancellationToken)
        {
            const int bufferSize = 1024 * 1024; // 1 MB buffer for better performance

            using (var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize))
            using (var destStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
            {
                var buffer = new byte[bufferSize];
                int bytesRead;

                while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }

                    destStream.Write(buffer, 0, bytesRead);
                    bytesWrittenCallback?.Invoke(bytesRead);
                }
            }
        }

        /// <summary>
        /// Gets the total size of a directory in bytes
        /// </summary>
        public static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            long size = 0;
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    size += new FileInfo(file).Length;
                }
                catch
                {
                    // Ignore files we can't access
                }
            }
            return size;
        }

        /// <summary>
        /// Formats a byte size to a human-readable string
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int suffixIndex = 0;
            double size = bytes;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return $"{size:0.##} {suffixes[suffixIndex]}";
        }
    }
}
