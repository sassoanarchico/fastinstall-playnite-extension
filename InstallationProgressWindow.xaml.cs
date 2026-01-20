using System;
using System.Threading;
using System.Windows;
using Playnite.SDK;

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
        private readonly Action onPauseRequested;
        private readonly Action onResumeRequested;
        private bool isCompleted = false;
        private bool isPaused = false;


        public InstallationProgressWindow(string gameName, CancellationTokenSource cts, Action onCancel = null, Action onPause = null, Action onResume = null, bool startQueued = false)
        {
            InitializeComponent();
            
            cancellationTokenSource = cts;
            onCancelRequested = onCancel;
            onPauseRequested = onPause;
            onResumeRequested = onResume;
            
            GameNameText.Text = gameName;
            StatusText.Text = startQueued
                ? ResourceProvider.GetString("LOCFastInstall_Progress_Status_PreparingQueued")
                : ResourceProvider.GetString("LOCFastInstall_Progress_Status_PreparingCopy");
            
            // Show/hide pause button based on availability
            if (onPauseRequested != null && onResumeRequested != null)
            {
                PauseButton.Visibility = Visibility.Visible;
            }
            else
            {
                PauseButton.Visibility = Visibility.Collapsed;
            }
            
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
            
            StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Progress_Status_Copying");
        }

        /// <summary>
        /// Shows the window as completed successfully
        /// </summary>
        public void ShowCompleted()
        {
            isCompleted = true;
            
            MainProgressBar.Value = 100;
            ProgressPercentText.Text = "100%";
            StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Progress_Status_Completed");
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            
            CancelButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Close");
            CancelButton.Background = System.Windows.Media.Brushes.ForestGreen;
            
            CurrentFileText.Text = ResourceProvider.GetString("LOCFastInstall_Progress_Status_AllFilesCopied");
            RemainingText.Text = "00:00";
        }

        /// <summary>
        /// Shows the window as failed
        /// </summary>
        public void ShowError(string errorMessage)
        {
            isCompleted = true;
            
            var fmt = ResourceProvider.GetString("LOCFastInstall_Progress_Status_ErrorFormat");
            StatusText.Text = string.Format(fmt, errorMessage);
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            
            CancelButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Close");
            CancelButton.Background = System.Windows.Media.Brushes.Gray;
        }

        /// <summary>
        /// Shows the window as cancelled
        /// </summary>
        public void ShowCancelled(bool showCleaningUp = true)
        {
            isCompleted = true;

            StatusText.Text = showCleaningUp
                ? ResourceProvider.GetString("LOCFastInstall_Progress_Status_CancelledCleaningUp")
                : ResourceProvider.GetString("LOCFastInstall_Progress_Status_Cancelled");
            StatusText.Foreground = System.Windows.Media.Brushes.Orange;
            
            CancelButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Close");
            CancelButton.Background = System.Windows.Media.Brushes.Gray;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (isCompleted)
                return;
            
            if (!isPaused)
            {
                // Pause
                isPaused = true;
                PauseButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Resume");
                PauseButton.Background = System.Windows.Media.Brushes.Green;
                StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Progress_Status_Pausing");
                StatusText.Foreground = System.Windows.Media.Brushes.Yellow;
                onPauseRequested?.Invoke();
            }
            else
            {
                // Resume
                isPaused = false;
                PauseButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Pause");
                PauseButton.Background = System.Windows.Media.Brushes.Orange;
                StatusText.Text = ResourceProvider.GetString("LOCFastInstall_Progress_Status_Resuming");
                StatusText.Foreground = System.Windows.Media.Brushes.White;
                onResumeRequested?.Invoke();
            }
        }

        /// <summary>
        /// Updates the pause button state when installation is paused externally
        /// </summary>
        public void UpdatePauseState(bool paused)
        {
            isPaused = paused;
            if (paused)
            {
                PauseButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Resume");
                PauseButton.Background = System.Windows.Media.Brushes.Green;
            }
            else
            {
                PauseButton.Content = ResourceProvider.GetString("LOCFastInstall_Progress_Button_Pause");
                PauseButton.Background = System.Windows.Media.Brushes.Orange;
            }
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
                ResourceProvider.GetString("LOCFastInstall_Progress_ConfirmCancel_Message"),
                ResourceProvider.GetString("LOCFastInstall_Progress_ConfirmCancel_Title"),
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
                    ResourceProvider.GetString("LOCFastInstall_Progress_ClosingInProgress_Message"),
                    ResourceProvider.GetString("LOCFastInstall_Progress_ClosingInProgress_Title"),
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
