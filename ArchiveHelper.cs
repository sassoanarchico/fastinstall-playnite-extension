using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace FastInstall
{
    /// <summary>
    /// Helper class for extracting archives (ZIP, RAR, 7Z) using 7-Zip
    /// </summary>
    public static class ArchiveHelper
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>
        /// Supported archive extensions
        /// </summary>
        public static readonly string[] SupportedExtensions = { ".zip", ".rar", ".7z", ".001" };

        /// <summary>
        /// Checks if a file is a supported archive format
        /// </summary>
        public static bool IsArchiveFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            return SupportedExtensions.Contains(extension);
        }

        /// <summary>
        /// Checks if a directory contains archive files
        /// </summary>
        public static bool ContainsArchives(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return false;

            try
            {
                var files = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);
                return files.Any(f => IsArchiveFile(f));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Finds the first archive file in a directory
        /// </summary>
        public static string FindArchiveFile(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return null;

            try
            {
                var files = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);
                return files.FirstOrDefault(f => IsArchiveFile(f));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts an archive to a destination directory using 7-Zip
        /// </summary>
        public static async Task<bool> ExtractArchive(
            string archivePath,
            string destinationPath,
            string sevenZipPath,
            Action<string> progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(sevenZipPath) || !File.Exists(sevenZipPath))
                    {
                        logger.Error($"FastInstall: 7-Zip executable not found at: {sevenZipPath}");
                        throw new FileNotFoundException($"7-Zip executable not found. Please configure 7-Zip path in settings.");
                    }

                    if (!File.Exists(archivePath))
                    {
                        logger.Error($"FastInstall: Archive file not found: {archivePath}");
                        throw new FileNotFoundException($"Archive file not found: {archivePath}");
                    }

                    // Ensure destination directory exists
                    Directory.CreateDirectory(destinationPath);

                    progressCallback?.Invoke($"Extracting archive: {Path.GetFileName(archivePath)}...");

                    // Prepare 7-Zip command line arguments
                    // x = extract with full paths
                    // -o = output directory (no space after -o!)
                    // -y = assume yes on all queries
                    // -bb = show progress
                    var arguments = $"x \"{archivePath}\" -o\"{destinationPath}\" -y -bb0";

                    var processStartInfo = new ProcessStartInfo
                    {
                        FileName = sevenZipPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    logger.Info($"FastInstall: Extracting '{archivePath}' to '{destinationPath}' using 7-Zip");

                    using (var process = Process.Start(processStartInfo))
                    {
                        if (process == null)
                        {
                            throw new Exception("Failed to start 7-Zip process");
                        }

                        // Wait for completion with cancellation support
                        var cancellationTask = Task.Run(() =>
                        {
                            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                            {
                                Thread.Sleep(100);
                            }
                            if (cancellationToken.IsCancellationRequested && !process.HasExited)
                            {
                                try
                                {
                                    process.Kill();
                                }
                                catch { }
                            }
                        });

                        process.WaitForExit();
                        cancellationTask.Wait();

                        if (cancellationToken.IsCancellationRequested)
                        {
                            logger.Info($"FastInstall: Archive extraction cancelled for '{archivePath}'");
                            return false;
                        }

                        if (process.ExitCode != 0)
                        {
                            var errorOutput = process.StandardError.ReadToEnd();
                            logger.Error($"FastInstall: 7-Zip extraction failed with exit code {process.ExitCode}: {errorOutput}");
                            throw new Exception($"Archive extraction failed. Exit code: {process.ExitCode}");
                        }

                        logger.Info($"FastInstall: Successfully extracted '{archivePath}' to '{destinationPath}'");
                        progressCallback?.Invoke("Extraction completed.");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, $"FastInstall: Error extracting archive '{archivePath}'");
                    throw;
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Gets the size of an archive (for progress estimation)
        /// </summary>
        public static long GetArchiveSize(string archivePath)
        {
            try
            {
                if (File.Exists(archivePath))
                {
                    return new FileInfo(archivePath).Length;
                }
            }
            catch { }
            return 0;
        }
    }
}
