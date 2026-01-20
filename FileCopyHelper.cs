using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

                    // Check existing files to support resume
                    long existingBytes = 0;
                    int existingFiles = 0;
                    foreach (var sourceFile in allFiles)
                    {
                        var relativePath = sourceFile.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar);
                        var destFile = Path.Combine(destinationPath, relativePath);
                        
                        if (File.Exists(destFile))
                        {
                            try
                            {
                                var sourceInfo = new FileInfo(sourceFile);
                                var destInfo = new FileInfo(destFile);
                                
                                // If file exists and has same size, consider it already copied
                                if (sourceInfo.Length == destInfo.Length)
                                {
                                    existingBytes += sourceInfo.Length;
                                    existingFiles++;
                                }
                            }
                            catch
                            {
                                // If we can't check, assume file needs to be copied
                            }
                        }
                    }
                    
                    // Initialize progress with existing files
                    progressInfo.CopiedBytes = existingBytes;
                    progressInfo.FilesCopied = existingFiles;
                    
                    logger.Info($"FastInstall: Resuming copy - {existingFiles} files already copied ({FormatBytes(existingBytes)})");

                    // Copy files with progress tracking
                    long lastUpdateBytes = existingBytes;
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

                        // Skip file if it already exists and has correct size (resume support)
                        bool skipFile = false;
                        if (File.Exists(destFile))
                        {
                            try
                            {
                                var destInfo = new FileInfo(destFile);
                                if (fileInfo.Length == destInfo.Length)
                                {
                                    skipFile = true;
                                    // Update progress as if file was copied
                                    progressInfo.CopiedBytes += fileInfo.Length;
                                    progressInfo.FilesCopied++;
                                    
                                    // Update progress display
                                    var timeSinceLastUpdate = (DateTime.Now - lastUpdateTime).TotalMilliseconds;
                                    if (timeSinceLastUpdate >= updateIntervalMs)
                                    {
                                        var bytesSinceLastUpdate = progressInfo.CopiedBytes - lastUpdateBytes;
                                        progressInfo.SpeedBytesPerSecond = bytesSinceLastUpdate / (timeSinceLastUpdate / 1000.0);

                                        if (progressInfo.SpeedBytesPerSecond > 0)
                                        {
                                            var remainingBytes = progressInfo.TotalBytes - progressInfo.CopiedBytes;
                                            var remainingSeconds = remainingBytes / progressInfo.SpeedBytesPerSecond;
                                            progressInfo.EstimatedRemaining = TimeSpan.FromSeconds(remainingSeconds);
                                        }

                                        progressInfo.PercentComplete = (int)((progressInfo.CopiedBytes * 100) / progressInfo.TotalBytes);

                                        lastUpdateBytes = progressInfo.CopiedBytes;
                                        lastUpdateTime = DateTime.Now;

                                        progressCallback?.Invoke(progressInfo);
                                    }
                                }
                            }
                            catch
                            {
                                // If we can't check, copy the file
                            }
                        }

                        if (!skipFile)
                        {
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

            // Check if file already exists and has correct size (resume support)
            if (File.Exists(destFile))
            {
                try
                {
                    var existingInfo = new FileInfo(destFile);
                    if (existingInfo.Length == fileSize)
                    {
                        // File already exists and has correct size - skip copy
                        bytesWrittenCallback?.Invoke(fileSize);
                        return;
                    }
                }
                catch
                {
                    // If we can't check, proceed with copy
                }
            }

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

        /// <summary>
        /// Gets the available free space on the drive containing the specified path
        /// </summary>
        public static long GetAvailableFreeSpace(string path)
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)));
                return driveInfo.AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, $"FastInstall: Could not get free space for path '{path}'");
                return -1; // Return -1 to indicate error
            }
        }

        /// <summary>
        /// Checks if there is enough free space on the destination drive for the installation
        /// Returns true if there's enough space, false otherwise
        /// </summary>
        public static bool CheckDiskSpace(string sourcePath, string destinationPath, out long requiredBytes, out long availableBytes)
        {
            requiredBytes = GetDirectorySize(sourcePath);
            availableBytes = GetAvailableFreeSpace(destinationPath);

            if (availableBytes < 0)
            {
                // Could not determine free space, assume it's OK (user will get error during copy if not)
                return true;
            }

            // Add 10% buffer for safety
            var requiredWithBuffer = (long)(requiredBytes * 1.1);
            return availableBytes >= requiredWithBuffer;
        }

        /// <summary>
        /// Result of an integrity check operation
        /// </summary>
        public class IntegrityCheckResult
        {
            public bool IsValid { get; set; }
            public int TotalFiles { get; set; }
            public int VerifiedFiles { get; set; }
            public int MissingFiles { get; set; }
            public int MismatchedFiles { get; set; }
            public List<string> MissingFilePaths { get; set; } = new List<string>();
            public List<string> MismatchedFilePaths { get; set; } = new List<string>();
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Verifies the integrity of copied files by comparing source and destination
        /// Checks file existence and sizes (quick check)
        /// </summary>
        public static IntegrityCheckResult VerifyCopyIntegrity(
            string sourcePath,
            string destinationPath,
            Action<string> progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var result = new IntegrityCheckResult();

            try
            {
                if (!Directory.Exists(sourcePath))
                {
                    result.ErrorMessage = string.Format(ResourceProvider.GetString("LOCFastInstall_Integrity_SourceDirMissingFormat"), sourcePath);
                    return result;
                }

                if (!Directory.Exists(destinationPath))
                {
                    result.ErrorMessage = string.Format(ResourceProvider.GetString("LOCFastInstall_Integrity_DestinationDirMissingFormat"), destinationPath);
                    return result;
                }

                progressCallback?.Invoke(ResourceProvider.GetString("LOCFastInstall_Integrity_VerifyingAll"));

                var sourceFiles = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories).ToList();
                result.TotalFiles = sourceFiles.Count;

                foreach (var sourceFile in sourceFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.ErrorMessage = ResourceProvider.GetString("LOCFastInstall_IntegrityCheck_CancelledByUser");
                        return result;
                    }

                    var relativePath = sourceFile.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar);
                    var destFile = Path.Combine(destinationPath, relativePath);

                    progressCallback?.Invoke(string.Format(ResourceProvider.GetString("LOCFastInstall_Integrity_VerifyingFileFormat"), relativePath));

                    // Check if file exists
                    if (!File.Exists(destFile))
                    {
                        result.MissingFiles++;
                        result.MissingFilePaths.Add(relativePath);
                        logger.Warn($"FastInstall: Missing file in destination: {relativePath}");
                        continue;
                    }

                    // Check file sizes
                    try
                    {
                        var sourceSize = new FileInfo(sourceFile).Length;
                        var destSize = new FileInfo(destFile).Length;

                        if (sourceSize != destSize)
                        {
                            result.MismatchedFiles++;
                            result.MismatchedFilePaths.Add(relativePath);
                            logger.Warn($"FastInstall: Size mismatch for {relativePath}: source={sourceSize}, dest={destSize}");
                        }
                        else
                        {
                            result.VerifiedFiles++;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, $"FastInstall: Could not verify file {relativePath}");
                        result.MismatchedFiles++;
                        result.MismatchedFilePaths.Add(relativePath);
                    }
                }

                result.IsValid = result.MissingFiles == 0 && result.MismatchedFiles == 0;

                if (result.IsValid)
                {
                    logger.Info($"FastInstall: Integrity check passed. Verified {result.VerifiedFiles}/{result.TotalFiles} files");
                }
                else
                {
                    logger.Warn($"FastInstall: Integrity check failed. Missing: {result.MissingFiles}, Mismatched: {result.MismatchedFiles}");
                }

                return result;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FastInstall: Error during integrity check");
                result.ErrorMessage = $"Errore durante la verifica: {ex.Message}";
                return result;
            }
        }
    }
}
