using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FastInstall
{
    /// <summary>
    /// Represents information about a cloud file or folder
    /// </summary>
    public class CloudFileInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string MimeType { get; set; }
        public long Size { get; set; }
        public bool IsFolder { get; set; }
        public string DownloadUrl { get; set; }
        public string ParentId { get; set; }
        public DateTime? ModifiedTime { get; set; }

        /// <summary>
        /// Formatted size string (e.g., "1.5 GB")
        /// </summary>
        public string SizeFormatted
        {
            get
            {
                if (Size < 1024) return $"{Size} B";
                if (Size < 1024 * 1024) return $"{Size / 1024.0:F1} KB";
                if (Size < 1024 * 1024 * 1024) return $"{Size / (1024.0 * 1024.0):F1} MB";
                return $"{Size / (1024.0 * 1024.0 * 1024.0):F2} GB";
            }
        }
    }

    /// <summary>
    /// Progress information for cloud downloads
    /// </summary>
    public class CloudDownloadProgress
    {
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string CurrentFile { get; set; }

        public int PercentComplete => TotalBytes > 0 ? (int)(BytesDownloaded * 100 / TotalBytes) : 0;

        public string DownloadedFormatted
        {
            get
            {
                if (BytesDownloaded < 1024) return $"{BytesDownloaded} B";
                if (BytesDownloaded < 1024 * 1024) return $"{BytesDownloaded / 1024.0:F1} KB";
                if (BytesDownloaded < 1024 * 1024 * 1024) return $"{BytesDownloaded / (1024.0 * 1024.0):F1} MB";
                return $"{BytesDownloaded / (1024.0 * 1024.0 * 1024.0):F2} GB";
            }
        }

        public string TotalFormatted
        {
            get
            {
                if (TotalBytes < 1024) return $"{TotalBytes} B";
                if (TotalBytes < 1024 * 1024) return $"{TotalBytes / 1024.0:F1} KB";
                if (TotalBytes < 1024 * 1024 * 1024) return $"{TotalBytes / (1024.0 * 1024.0):F1} MB";
                return $"{TotalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            }
        }

        public string SpeedFormatted
        {
            get
            {
                if (SpeedBytesPerSecond < 1024) return $"{SpeedBytesPerSecond:F0} B/s";
                if (SpeedBytesPerSecond < 1024 * 1024) return $"{SpeedBytesPerSecond / 1024.0:F1} KB/s";
                return $"{SpeedBytesPerSecond / (1024.0 * 1024.0):F1} MB/s";
            }
        }

        public string RemainingFormatted
        {
            get
            {
                if (SpeedBytesPerSecond <= 0 || BytesDownloaded >= TotalBytes)
                    return "--:--";

                var remaining = (TotalBytes - BytesDownloaded) / SpeedBytesPerSecond;
                var ts = TimeSpan.FromSeconds(remaining);
                if (ts.TotalHours >= 1)
                    return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
                return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
            }
        }

        public string ElapsedFormatted
        {
            get
            {
                if (Elapsed.TotalHours >= 1)
                    return $"{(int)Elapsed.TotalHours}:{Elapsed.Minutes:D2}:{Elapsed.Seconds:D2}";
                return $"{Elapsed.Minutes:D2}:{Elapsed.Seconds:D2}";
            }
        }
    }

    /// <summary>
    /// Result of parsing a cloud storage link
    /// </summary>
    public class CloudLinkParseResult
    {
        public bool IsValid { get; set; }
        public string FileId { get; set; }
        public bool IsFolder { get; set; }
        public string ErrorMessage { get; set; }
        public string OriginalUrl { get; set; }
    }

    /// <summary>
    /// Enum for supported cloud storage providers
    /// </summary>
    public enum CloudProvider
    {
        GoogleDrive
    }

    /// <summary>
    /// Interface for cloud storage providers
    /// Implementations handle provider-specific API calls and authentication
    /// </summary>
    public interface ICloudStorageProvider
    {
        /// <summary>
        /// The provider type
        /// </summary>
        CloudProvider Provider { get; }

        /// <summary>
        /// Display name for the provider
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Whether this provider requires an API key
        /// </summary>
        bool RequiresApiKey { get; }

        /// <summary>
        /// Set the API key for this provider
        /// </summary>
        void SetApiKey(string apiKey);

        /// <summary>
        /// Parse a share link to extract file/folder ID
        /// </summary>
        CloudLinkParseResult ParseLink(string url);

        /// <summary>
        /// Get information about a file or folder
        /// </summary>
        Task<CloudFileInfo> GetFileInfoAsync(string fileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// List files in a folder
        /// </summary>
        Task<List<CloudFileInfo>> ListFilesAsync(string folderId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Download a file to the specified local path
        /// </summary>
        Task<bool> DownloadFileAsync(
            string fileId,
            string destinationPath,
            Action<CloudDownloadProgress> progressCallback = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Get the direct download URL for a file
        /// </summary>
        string GetDownloadUrl(string fileId);

        /// <summary>
        /// Test if the provider is properly configured and can connect
        /// </summary>
        Task<bool> TestConnectionAsync(string testFileOrFolderId = null);
    }
}
