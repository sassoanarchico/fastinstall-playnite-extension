using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;

namespace FastInstall
{
    /// <summary>
    /// Google Drive implementation of ICloudStorageProvider
    /// Supports both direct file links and shared folder links
    /// </summary>
    public class GoogleDriveProvider : ICloudStorageProvider
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private string apiKey;
        private readonly HttpClient httpClient;

        // Regex patterns for parsing Google Drive URLs
        private static readonly Regex FolderIdPattern = new Regex(
            @"drive\.google\.com/drive/(?:u/\d+/)?folders/([a-zA-Z0-9_-]+)",
            RegexOptions.Compiled);
        private static readonly Regex FileIdPattern = new Regex(
            @"drive\.google\.com/file/d/([a-zA-Z0-9_-]+)",
            RegexOptions.Compiled);
        private static readonly Regex OpenIdPattern = new Regex(
            @"drive\.google\.com/open\?id=([a-zA-Z0-9_-]+)",
            RegexOptions.Compiled);
        private static readonly Regex UcIdPattern = new Regex(
            @"drive\.google\.com/uc\?.*id=([a-zA-Z0-9_-]+)",
            RegexOptions.Compiled);

        public CloudProvider Provider => CloudProvider.GoogleDrive;
        public string DisplayName => "Google Drive";
        public bool RequiresApiKey => true; // Required for folder listing

        public GoogleDriveProvider()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };
            httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(30) // Long timeout for large files
            };
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        }

        public void SetApiKey(string key)
        {
            apiKey = key;
            logger.Info("GoogleDriveProvider: API key configured");
        }

        public CloudLinkParseResult ParseLink(string url)
        {
            var result = new CloudLinkParseResult { OriginalUrl = url };

            if (string.IsNullOrWhiteSpace(url))
            {
                result.IsValid = false;
                result.ErrorMessage = "URL is empty";
                return result;
            }

            // Try folder pattern
            var folderMatch = FolderIdPattern.Match(url);
            if (folderMatch.Success)
            {
                result.IsValid = true;
                result.FileId = folderMatch.Groups[1].Value;
                result.IsFolder = true;
                return result;
            }

            // Try file pattern
            var fileMatch = FileIdPattern.Match(url);
            if (fileMatch.Success)
            {
                result.IsValid = true;
                result.FileId = fileMatch.Groups[1].Value;
                result.IsFolder = false;
                return result;
            }

            // Try open?id= pattern
            var openMatch = OpenIdPattern.Match(url);
            if (openMatch.Success)
            {
                result.IsValid = true;
                result.FileId = openMatch.Groups[1].Value;
                result.IsFolder = false; // Could be either, will determine later
                return result;
            }

            // Try uc?id= pattern
            var ucMatch = UcIdPattern.Match(url);
            if (ucMatch.Success)
            {
                result.IsValid = true;
                result.FileId = ucMatch.Groups[1].Value;
                result.IsFolder = false;
                return result;
            }

            // Check if it's just a raw ID
            if (Regex.IsMatch(url, @"^[a-zA-Z0-9_-]{20,}$"))
            {
                result.IsValid = true;
                result.FileId = url;
                result.IsFolder = false; // Assume file
                return result;
            }

            result.IsValid = false;
            result.ErrorMessage = "Invalid Google Drive URL format";
            return result;
        }

        public async Task<CloudFileInfo> GetFileInfoAsync(string fileId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                logger.Warn("GoogleDriveProvider: API key not set, cannot get file info");
                return null;
            }

            try
            {
                var url = $"https://www.googleapis.com/drive/v3/files/{fileId}?fields=id,name,mimeType,size,modifiedTime&key={apiKey}";
                var response = await httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    logger.Error($"GoogleDriveProvider: Failed to get file info. Status: {response.StatusCode}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                return ParseFileInfoJson(json);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GoogleDriveProvider: Error getting file info");
                return null;
            }
        }

        public async Task<List<CloudFileInfo>> ListFilesAsync(string folderId, CancellationToken cancellationToken = default)
        {
            var files = new List<CloudFileInfo>();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                logger.Warn("GoogleDriveProvider: API key not set, cannot list files");
                throw new Exception("API key is not configured. Please set your Google Drive API key in settings.");
            }

            logger.Info($"GoogleDriveProvider: Listing files in folder {folderId}");

            try
            {
                string pageToken = null;
                do
                {
                    var url = $"https://www.googleapis.com/drive/v3/files?q='{folderId}'+in+parents&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime)&pageSize=1000&key={apiKey}";
                    if (!string.IsNullOrEmpty(pageToken))
                    {
                        url += $"&pageToken={pageToken}";
                    }

                    logger.Debug($"GoogleDriveProvider: Requesting URL (key hidden): {url.Replace(apiKey, "***")}");

                    var response = await httpClient.GetAsync(url, cancellationToken);
                    var responseContent = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        logger.Error($"GoogleDriveProvider: Failed to list files. Status: {response.StatusCode}, Response: {responseContent}");
                        
                        // Try to extract error message from response
                        var errorMatch = System.Text.RegularExpressions.Regex.Match(responseContent, @"""message""\s*:\s*""([^""]+)""");
                        var errorMessage = errorMatch.Success ? errorMatch.Groups[1].Value : $"HTTP {response.StatusCode}";
                        
                        throw new Exception($"Google Drive API error: {errorMessage}");
                    }

                    var (parsedFiles, nextToken) = ParseFileListJson(responseContent);
                    files.AddRange(parsedFiles);
                    pageToken = nextToken;

                    logger.Debug($"GoogleDriveProvider: Got {parsedFiles.Count} files in this page, total so far: {files.Count}");

                } while (!string.IsNullOrEmpty(pageToken) && !cancellationToken.IsCancellationRequested);

                logger.Info($"GoogleDriveProvider: Listed {files.Count} files in folder {folderId}");
            }
            catch (HttpRequestException ex)
            {
                logger.Error(ex, "GoogleDriveProvider: Network error listing files");
                throw new Exception($"Network error: {ex.Message}. Check your internet connection.");
            }
            catch (Exception ex) when (!(ex is Exception))
            {
                logger.Error(ex, "GoogleDriveProvider: Error listing files");
                throw;
            }

            return files;
        }

        public string GetDownloadUrl(string fileId)
        {
            // Try using API v3 if we have an API key (bypasses virus scan warning)
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media&key={apiKey}";
            }
            
            // Fallback to standard download URL
            return $"https://drive.google.com/uc?export=download&id={fileId}";
        }

        public async Task<bool> DownloadFileAsync(
            string fileId,
            string destinationPath,
            Action<CloudDownloadProgress> progressCallback = null,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var progress = new CloudDownloadProgress();

            try
            {
                // Get file info first to know the size
                var fileInfo = await GetFileInfoAsync(fileId, cancellationToken);
                if (fileInfo != null)
                {
                    progress.TotalBytes = fileInfo.Size;
                    progress.CurrentFile = fileInfo.Name;
                }

                // Ensure destination directory exists
                var destDir = Path.GetDirectoryName(destinationPath);
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // Start download - try API v3 first if we have API key, otherwise use standard URL
                string downloadUrl = GetDownloadUrl(fileId);
                bool usingApiV3 = downloadUrl.Contains("googleapis.com");
                logger.Info($"GoogleDriveProvider: Download URL: {downloadUrl.Replace(apiKey ?? "", "***")}");
                logger.Info($"GoogleDriveProvider: Using API v3: {usingApiV3}");
                logger.Info($"GoogleDriveProvider: File ID: {fileId}");
                logger.Info($"GoogleDriveProvider: Destination: {destinationPath}");

                HttpResponseMessage response = null;
                try
                {
                    response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    
                    // If API v3 failed, try falling back to standard download URL
                    if (usingApiV3 && !response.IsSuccessStatusCode && 
                        (response.StatusCode == System.Net.HttpStatusCode.Forbidden || response.StatusCode == System.Net.HttpStatusCode.Unauthorized))
                    {
                        logger.Info("GoogleDriveProvider: API v3 failed, falling back to standard download URL");
                        response.Dispose();
                        downloadUrl = $"https://drive.google.com/uc?export=download&id={fileId}";
                        usingApiV3 = false;
                        response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    response?.Dispose();
                    logger.Error(httpEx, $"GoogleDriveProvider: Network error connecting to Google Drive");
                    throw new Exception($"Network error: {httpEx.Message}. Check your internet connection.");
                }

                logger.Info($"GoogleDriveProvider: Response status: {response.StatusCode}");
                logger.Info($"GoogleDriveProvider: Content-Type: {response.Content.Headers.ContentType?.MediaType}");

                using (response)
                {
                    // Check for error response
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        logger.Error($"GoogleDriveProvider: Download failed with status {response.StatusCode}");
                        logger.Error($"GoogleDriveProvider: Error content: {errorContent.Substring(0, Math.Min(500, errorContent.Length))}");
                        
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            throw new Exception("File not found. Make sure the file exists and is publicly shared.");
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        {
                            throw new Exception("Access denied. Make sure the file is shared as 'Anyone with the link'.");
                        }
                        else
                        {
                            throw new Exception($"Download failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
                        }
                    }

                    // Check for virus scan warning page (for large files)
                    // Skip this check if we're using API v3 (it bypasses virus scan warning)
                    if (!usingApiV3 && response.Content.Headers.ContentType?.MediaType == "text/html")
                    {
                        logger.Info("GoogleDriveProvider: HTML response detected - might be virus scan warning or error page");
                        
                        // Read the HTML to check what it is
                        var htmlContent = await response.Content.ReadAsStringAsync();
                        logger.Debug($"GoogleDriveProvider: HTML content (first 1000 chars): {htmlContent.Substring(0, Math.Min(1000, htmlContent.Length))}");
                        
                        // Check if it's the virus scan warning
                        if (htmlContent.Contains("virus scan") || htmlContent.Contains("Virus scan warning") || 
                            htmlContent.Contains("download anyway") || htmlContent.Contains("This file is too large") ||
                            htmlContent.Contains("Google Drive can't scan this file"))
                        {
                            logger.Info("GoogleDriveProvider: Large file detected, handling virus scan confirmation");
                            
                            // Try to extract confirm token from the HTML we already have
                            string confirmUrl = ExtractConfirmUrlFromHtml(htmlContent, fileId);
                            
                            // If we couldn't extract it, try the old method
                            if (string.IsNullOrEmpty(confirmUrl))
                            {
                                logger.Info("GoogleDriveProvider: Could not extract confirm URL from HTML, trying alternative method");
                                confirmUrl = await GetConfirmDownloadUrlAsync(fileId, cancellationToken);
                            }
                            
                            // If still no URL, try with confirm=t
                            if (string.IsNullOrEmpty(confirmUrl))
                            {
                                logger.Info("GoogleDriveProvider: Using fallback confirm=t method");
                                confirmUrl = $"https://drive.google.com/uc?export=download&confirm=t&id={fileId}";
                            }
                            
                            if (!string.IsNullOrEmpty(confirmUrl))
                            {
                                logger.Info($"GoogleDriveProvider: Using confirm URL: {confirmUrl.Replace(fileId, "FILEID")}");
                                return await DownloadWithProgressAsync(confirmUrl, destinationPath, progress, progressCallback, stopwatch, cancellationToken);
                            }
                            else
                            {
                                logger.Error("GoogleDriveProvider: Failed to get confirm download URL");
                                throw new Exception("Failed to handle large file download confirmation. The file might be too large or require manual download.");
                            }
                        }
                        else if (htmlContent.Contains("not found") || htmlContent.Contains("404") || htmlContent.Contains("does not exist"))
                        {
                            throw new Exception("File not found on Google Drive. Check if the file still exists.");
                        }
                        else if (htmlContent.Contains("access") || htmlContent.Contains("permission") || htmlContent.Contains("denied"))
                        {
                            throw new Exception("Access denied. Make sure the file is shared as 'Anyone with the link'.");
                        }
                        else
                        {
                            throw new Exception($"Unexpected response from Google Drive. The file might not be publicly shared.\n\nResponse: {htmlContent.Substring(0, Math.Min(200, htmlContent.Length))}");
                        }
                    }

                    // Get content length if available
                    if (response.Content.Headers.ContentLength.HasValue)
                    {
                        progress.TotalBytes = response.Content.Headers.ContentLength.Value;
                    }

                    // Download with progress
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        int bytesRead;
                        long totalRead = 0;
                        var lastProgressUpdate = DateTime.Now;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                            totalRead += bytesRead;

                            // Update progress every 100ms
                            if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds >= 100)
                            {
                                progress.BytesDownloaded = totalRead;
                                progress.Elapsed = stopwatch.Elapsed;
                                progress.SpeedBytesPerSecond = totalRead / stopwatch.Elapsed.TotalSeconds;
                                progressCallback?.Invoke(progress);
                                lastProgressUpdate = DateTime.Now;
                            }
                        }

                        // Final progress update
                        progress.BytesDownloaded = totalRead;
                        progress.Elapsed = stopwatch.Elapsed;
                        progress.SpeedBytesPerSecond = totalRead / stopwatch.Elapsed.TotalSeconds;
                        progressCallback?.Invoke(progress);
                    }
                }

                logger.Info($"GoogleDriveProvider: Download completed in {stopwatch.Elapsed.TotalSeconds:F1}s");
                return true;
            }
            catch (OperationCanceledException)
            {
                logger.Info("GoogleDriveProvider: Download cancelled by user");
                // Clean up partial file
                if (File.Exists(destinationPath))
                {
                    try { File.Delete(destinationPath); } catch { }
                }
                throw; // Re-throw so caller knows it was cancelled
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"GoogleDriveProvider: Download error: {ex.Message}");
                // Clean up partial file
                if (File.Exists(destinationPath))
                {
                    try { File.Delete(destinationPath); } catch { }
                }
                throw; // Re-throw with original message
            }
        }

        private async Task<bool> DownloadWithProgressAsync(
            string url,
            string destinationPath,
            CloudDownloadProgress progress,
            Action<CloudDownloadProgress> progressCallback,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            logger.Info($"GoogleDriveProvider: Downloading from confirm URL: {url.Replace("confirm=", "confirm=***")}");
            
            using (var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    logger.Error($"GoogleDriveProvider: Download failed with status {response.StatusCode}");
                    throw new Exception($"Download failed with HTTP {response.StatusCode}: {response.ReasonPhrase}");
                }

                // Check if we still got HTML (confirm URL didn't work)
                if (response.Content.Headers.ContentType?.MediaType == "text/html")
                {
                    var htmlPreview = await response.Content.ReadAsStringAsync();
                    response.Content.Dispose(); // Dispose the old content
                    
                    logger.Warn("GoogleDriveProvider: Still received HTML after confirm URL, trying alternative method");
                    
                    // Try one more time with a different approach - use the direct download with a cookie
                    // Extract file ID from URL
                    var fileIdMatch = Regex.Match(url, @"[?&]id=([^&]+)");
                    if (fileIdMatch.Success)
                    {
                        var fileId = fileIdMatch.Groups[1].Value;
                        // Try with a session-based approach
                        var directUrl = $"https://drive.google.com/uc?export=download&id={fileId}";
                        
                        // Create a new request with cookies from the previous response
                        var cookieContainer = new System.Net.CookieContainer();
                        var handler = new System.Net.Http.HttpClientHandler { CookieContainer = cookieContainer };
                        using (var cookieClient = new HttpClient(handler))
                        {
                            // First request to get cookies
                            await cookieClient.GetAsync($"https://drive.google.com/file/d/{fileId}/view", cancellationToken);
                            
                            // Now try download with cookies
                            using (var downloadResponse = await cookieClient.GetAsync(directUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                            {
                                if (downloadResponse.IsSuccessStatusCode && downloadResponse.Content.Headers.ContentType?.MediaType != "text/html")
                                {
                                    logger.Info("GoogleDriveProvider: Cookie-based download successful");
                                    return await DownloadStreamToFile(downloadResponse, destinationPath, progress, progressCallback, stopwatch, cancellationToken);
                                }
                            }
                        }
                    }
                    
                    throw new Exception("Unable to download file. Google Drive is still showing a warning page. The file might require manual download from the browser.");
                }

                return await DownloadStreamToFile(response, destinationPath, progress, progressCallback, stopwatch, cancellationToken);
            }
        }

        private async Task<bool> DownloadStreamToFile(
            HttpResponseMessage response,
            string destinationPath,
            CloudDownloadProgress progress,
            Action<CloudDownloadProgress> progressCallback,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            if (response.Content.Headers.ContentLength.HasValue)
            {
                progress.TotalBytes = response.Content.Headers.ContentLength.Value;
            }

            using (var contentStream = await response.Content.ReadAsStreamAsync())
            using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int bytesRead;
                long totalRead = 0;
                var lastProgressUpdate = DateTime.Now;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    totalRead += bytesRead;

                    if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds >= 100)
                    {
                        progress.BytesDownloaded = totalRead;
                        progress.Elapsed = stopwatch.Elapsed;
                        progress.SpeedBytesPerSecond = totalRead / stopwatch.Elapsed.TotalSeconds;
                        progressCallback?.Invoke(progress);
                        lastProgressUpdate = DateTime.Now;
                    }
                }

                progress.BytesDownloaded = totalRead;
                progress.Elapsed = stopwatch.Elapsed;
                progress.SpeedBytesPerSecond = totalRead / stopwatch.Elapsed.TotalSeconds;
                progressCallback?.Invoke(progress);
            }

            return true;
        }

        /// <summary>
        /// Extracts the confirm download URL from the virus scan warning HTML page
        /// </summary>
        private string ExtractConfirmUrlFromHtml(string html, string fileId)
        {
            try
            {
                logger.Debug("GoogleDriveProvider: Extracting confirm URL from HTML");
                logger.Debug($"GoogleDriveProvider: HTML length: {html.Length}");

                // Pattern 1: Look for the download form with id="downloadForm" or similar
                // Google Drive often uses: <form id="downloadForm" action="/uc?export=download&confirm=TOKEN&id=FILEID">
                var formMatch = Regex.Match(html, @"<form[^>]*action=[""']([^""']*uc[^""']*export[^""']*download[^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (formMatch.Success)
                {
                    var action = formMatch.Groups[1].Value.Replace("&amp;", "&").Replace("&#39;", "'");
                    logger.Info($"GoogleDriveProvider: Found form action: {action.Substring(0, Math.Min(100, action.Length))}...");
                    if (!action.StartsWith("http"))
                    {
                        return "https://drive.google.com" + action;
                    }
                    return action;
                }

                // Pattern 2: Look for confirm parameter in any URL (most common)
                // Try to find the longest match (Google Drive tokens can be quite long)
                var confirmMatches = Regex.Matches(html, @"confirm=([0-9A-Za-z_-]{4,})", RegexOptions.IgnoreCase);
                if (confirmMatches.Count > 0)
                {
                    // Take the first match (usually the correct one)
                    var confirm = confirmMatches[0].Groups[1].Value;
                    logger.Info($"GoogleDriveProvider: Found confirm token (length: {confirm.Length})");
                    return $"https://drive.google.com/uc?export=download&confirm={confirm}&id={fileId}";
                }

                // Pattern 3: Look for hidden input with name="confirm" or value containing token
                var hiddenInputMatch = Regex.Match(html, @"<input[^>]*name=[""']confirm[""'][^>]*value=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (hiddenInputMatch.Success)
                {
                    var confirm = hiddenInputMatch.Groups[1].Value;
                    logger.Info($"GoogleDriveProvider: Found confirm in hidden input: {confirm.Substring(0, Math.Min(10, confirm.Length))}...");
                    return $"https://drive.google.com/uc?export=download&confirm={confirm}&id={fileId}";
                }

                // Pattern 4: Look for JavaScript variable containing the token
                var jsVarMatch = Regex.Match(html, @"(?:var\s+)?confirm\s*=\s*[""']([0-9A-Za-z_-]+)[""']", RegexOptions.IgnoreCase);
                if (jsVarMatch.Success)
                {
                    var confirm = jsVarMatch.Groups[1].Value;
                    logger.Info($"GoogleDriveProvider: Found confirm in JavaScript: {confirm.Substring(0, Math.Min(10, confirm.Length))}...");
                    return $"https://drive.google.com/uc?export=download&confirm={confirm}&id={fileId}";
                }

                // Pattern 5: Look for href with download URL
                var hrefMatch = Regex.Match(html, @"href=[""'](/uc\?export=download[^""']+)[""']", RegexOptions.IgnoreCase);
                if (hrefMatch.Success)
                {
                    var href = hrefMatch.Groups[1].Value.Replace("&amp;", "&");
                    logger.Info("GoogleDriveProvider: Found href download URL");
                    return "https://drive.google.com" + href;
                }

                // Pattern 6: Look for window.location or similar JavaScript redirects
                var jsMatch = Regex.Match(html, @"window\.location\s*[=:]\s*[""']([^""']*uc[^""']*export[^""']*download[^""']*)[""']", RegexOptions.IgnoreCase);
                if (jsMatch.Success)
                {
                    var jsUrl = jsMatch.Groups[1].Value.Replace("&amp;", "&");
                    logger.Info("GoogleDriveProvider: Found JavaScript redirect URL");
                    if (!jsUrl.StartsWith("http"))
                    {
                        return "https://drive.google.com" + jsUrl;
                    }
                    return jsUrl;
                }

                // Pattern 7: Look for data attributes on download button
                var buttonMatch = Regex.Match(html, @"<[^>]*data-url=[""']([^""']*uc[^""']*export[^""']*download[^""']*)[""']", RegexOptions.IgnoreCase);
                if (buttonMatch.Success)
                {
                    var buttonUrl = buttonMatch.Groups[1].Value.Replace("&amp;", "&");
                    logger.Info("GoogleDriveProvider: Found button data-url");
                    if (!buttonUrl.StartsWith("http"))
                    {
                        return "https://drive.google.com" + buttonUrl;
                    }
                    return buttonUrl;
                }

                // Pattern 8: Look for onclick with download URL
                var onclickMatch = Regex.Match(html, @"onclick=[""'][^""']*[""']([^""']*uc[^""']*export[^""']*download[^""']*)[""']", RegexOptions.IgnoreCase);
                if (onclickMatch.Success)
                {
                    var onclickUrl = onclickMatch.Groups[1].Value.Replace("&amp;", "&");
                    logger.Info("GoogleDriveProvider: Found onclick URL");
                    if (!onclickUrl.StartsWith("http"))
                    {
                        return "https://drive.google.com" + onclickUrl;
                    }
                    return onclickUrl;
                }

                logger.Warn("GoogleDriveProvider: Could not extract confirm URL from HTML using any pattern");
                logger.Debug($"GoogleDriveProvider: HTML snippet (first 2000 chars): {html.Substring(0, Math.Min(2000, html.Length))}");
                return null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GoogleDriveProvider: Error extracting confirm URL from HTML");
                return null;
            }
        }

        private async Task<string> GetConfirmDownloadUrlAsync(string fileId, CancellationToken cancellationToken)
        {
            try
            {
                // First request to get the confirmation page
                var initialUrl = GetDownloadUrl(fileId);
                logger.Info($"GoogleDriveProvider: Fetching HTML page for confirm URL extraction: {initialUrl}");
                var html = await httpClient.GetStringAsync(initialUrl);

                // Try to extract from the HTML
                var confirmUrl = ExtractConfirmUrlFromHtml(html, fileId);
                if (!string.IsNullOrEmpty(confirmUrl))
                {
                    return confirmUrl;
                }

                // Fallback: just add confirm=t
                logger.Info("GoogleDriveProvider: Using fallback confirm=t");
                return $"https://drive.google.com/uc?export=download&confirm=t&id={fileId}";
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GoogleDriveProvider: Error getting confirm URL");
                // Return fallback URL even on error
                return $"https://drive.google.com/uc?export=download&confirm=t&id={fileId}";
            }
        }

        public async Task<bool> TestConnectionAsync(string testFileOrFolderId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    logger.Warn("GoogleDriveProvider: Cannot test connection without API key");
                    return false;
                }

                // Test by trying to access the API
                var url = $"https://www.googleapis.com/drive/v3/about?fields=user&key={apiKey}";
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    logger.Info("GoogleDriveProvider: Connection test successful");
                    return true;
                }

                logger.Error($"GoogleDriveProvider: Connection test failed with status {response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GoogleDriveProvider: Connection test error");
                return false;
            }
        }

        #region JSON Parsing (Simple implementation without external dependencies)

        private CloudFileInfo ParseFileInfoJson(string json)
        {
            try
            {
                var info = new CloudFileInfo();

                // Extract id
                var idMatch = Regex.Match(json, @"""id""\s*:\s*""([^""]+)""");
                if (idMatch.Success) info.Id = idMatch.Groups[1].Value;

                // Extract name
                var nameMatch = Regex.Match(json, @"""name""\s*:\s*""([^""]+)""");
                if (nameMatch.Success) info.Name = UnescapeJson(nameMatch.Groups[1].Value);

                // Extract mimeType
                var mimeMatch = Regex.Match(json, @"""mimeType""\s*:\s*""([^""]+)""");
                if (mimeMatch.Success)
                {
                    info.MimeType = mimeMatch.Groups[1].Value;
                    info.IsFolder = info.MimeType == "application/vnd.google-apps.folder";
                }

                // Extract size
                var sizeMatch = Regex.Match(json, @"""size""\s*:\s*""?(\d+)""?");
                if (sizeMatch.Success) info.Size = long.Parse(sizeMatch.Groups[1].Value);

                // Extract modifiedTime
                var timeMatch = Regex.Match(json, @"""modifiedTime""\s*:\s*""([^""]+)""");
                if (timeMatch.Success)
                {
                    if (DateTime.TryParse(timeMatch.Groups[1].Value, out var dt))
                        info.ModifiedTime = dt;
                }

                info.DownloadUrl = GetDownloadUrl(info.Id);
                return info;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GoogleDriveProvider: Error parsing file info JSON");
                return null;
            }
        }

        private (List<CloudFileInfo> files, string nextPageToken) ParseFileListJson(string json)
        {
            var files = new List<CloudFileInfo>();
            string nextToken = null;

            try
            {
                // Extract nextPageToken
                var tokenMatch = Regex.Match(json, @"""nextPageToken""\s*:\s*""([^""]+)""");
                if (tokenMatch.Success) nextToken = tokenMatch.Groups[1].Value;

                // Extract files array content
                var filesMatch = Regex.Match(json, @"""files""\s*:\s*\[(.*)\]", RegexOptions.Singleline);
                if (filesMatch.Success)
                {
                    var filesContent = filesMatch.Groups[1].Value;

                    // Split by file objects (this is a simplified approach)
                    var fileMatches = Regex.Matches(filesContent, @"\{[^{}]+\}");
                    foreach (Match fileMatch in fileMatches)
                    {
                        var fileInfo = ParseFileInfoJson(fileMatch.Value);
                        if (fileInfo != null && !string.IsNullOrEmpty(fileInfo.Id))
                        {
                            files.Add(fileInfo);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "GoogleDriveProvider: Error parsing file list JSON");
            }

            return (files, nextToken);
        }

        private string UnescapeJson(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            return input
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\/", "/")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        #endregion
    }
}
