using System;
using System.Threading;
using System.Windows;

namespace FastInstall
{
    /// <summary>
    /// Non-modal window for showing installation progress
    /// Allows Playnite to remain usable during installation
    /// </summary>
    public partial class InstallationProgressWindow : Window
    {
        private readonly CancellationTokenSource cancellationTokenSource;
        private readonly Action onCancelRequested;
        private bool isCompleted = false;

        public InstallationProgressWindow(string gameName, CancellationTokenSource cts, Action onCancel = null, bool startQueued = false)
        {
            InitializeComponent();
            
            cancellationTokenSource = cts;
            onCancelRequested = onCancel;
            
            GameNameText.Text = gameName;
            StatusText.Text = startQueued ? "Queued for installation..." : "Preparing to copy files...";
            
            // Handle window closing
            Closing += OnWindowClosing;
        }

        /// <summary>
        /// Updates the progress display with current copy status
        /// Must be called from UI thread
        /// </summary>
        public void UpdateProgress(CopyProgressInfo progress)
        {
            if (isCompleted) return;
            
            // Update progress bar
            MainProgressBar.Value = progress.PercentComplete;
            ProgressPercentText.Text = $"{progress.PercentComplete}%";
            
            // Update stats
            BytesProgressText.Text = $"{progress.CopiedFormatted} / {progress.TotalFormatted}";
            SpeedText.Text = progress.SpeedFormatted;
            FilesProgressText.Text = $"{progress.FilesCopied} / {progress.TotalFiles}";
            ElapsedText.Text = progress.ElapsedFormatted;
            RemainingText.Text = progress.RemainingFormatted;
            
            // Update current file
            if (!string.IsNullOrEmpty(progress.CurrentFile))
            {
                CurrentFileText.Text = progress.CurrentFile;
                CurrentFileText.ToolTip = progress.CurrentFile;
            }
            
            StatusText.Text = "Copying files...";
        }

        /// <summary>
        /// Shows the window as completed successfully
        /// </summary>
        public void ShowCompleted()
        {
            isCompleted = true;
            
            MainProgressBar.Value = 100;
            ProgressPercentText.Text = "100%";
            StatusText.Text = "Installation completed successfully!";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            
            CancelButton.Content = "Close";
            CancelButton.Background = System.Windows.Media.Brushes.ForestGreen;
            
            CurrentFileText.Text = "All files copied";
            RemainingText.Text = "00:00";
        }

        /// <summary>
        /// Shows the window as failed
        /// </summary>
        public void ShowError(string errorMessage)
        {
            isCompleted = true;
            
            StatusText.Text = $"Error: {errorMessage}";
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            
            CancelButton.Content = "Close";
            CancelButton.Background = System.Windows.Media.Brushes.Gray;
        }

        /// <summary>
        /// Shows the window as cancelled
        /// </summary>
        public void ShowCancelled(bool showCleaningUp = true)
        {
            isCompleted = true;

            StatusText.Text = showCleaningUp 
                ? "Installation cancelled. Cleaning up..." 
                : "Installation cancelled";
            StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            
            CancelButton.Content = "Close";
            CancelButton.Background = System.Windows.Media.Brushes.Gray;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (isCompleted)
            {
                Close();
                return;
            }
            
            // Ask for confirmation
            var result = MessageBox.Show(
                "Are you sure you want to cancel the installation?\n\nPartially copied files will be deleted.",
                "Cancel Installation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                ShowCancelled();
                cancellationTokenSource?.Cancel();
                onCancelRequested?.Invoke();
            }
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!isCompleted)
            {
                // Ask for confirmation before closing
                var result = MessageBox.Show(
                    "Installation is still in progress.\n\nDo you want to cancel and close?",
                    "Installation in Progress",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.Yes)
                {
                    cancellationTokenSource?.Cancel();
                    onCancelRequested?.Invoke();
                }
                else
                {
                    e.Cancel = true;
                }
            }
        }

        /// <summary>
        /// Allows closing the window after completion
        /// </summary>
        public void AllowClose()
        {
            isCompleted = true;
        }
    }
}
