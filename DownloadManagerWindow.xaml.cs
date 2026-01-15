using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Playnite.SDK.Models;

namespace FastInstall
{
    public partial class DownloadManagerWindow : Window
    {
        private readonly DispatcherTimer refreshTimer;
        private readonly ObservableCollection<DownloadItemViewModel> downloadItems;

        public DownloadManagerWindow()
        {
            InitializeComponent();
            
            downloadItems = new ObservableCollection<DownloadItemViewModel>();
            DownloadsDataGrid.ItemsSource = downloadItems;

            // Set up refresh timer (update every second)
            refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            refreshTimer.Tick += RefreshTimer_Tick;
            refreshTimer.Start();

            // Initial refresh
            RefreshDownloads();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshDownloads();
        }

        private void RefreshDownloads()
        {
            try
            {
                var instance = BackgroundInstallManager.Instance;
                if (instance == null)
                {
                    StatusText.Text = "Download manager not initialized";
                    downloadItems.Clear();
                    return;
                }

                var allJobs = instance.GetAllJobs();
                var queueInfo = instance.GetQueueInfo();

                // Update status text
                StatusText.Text = $"{queueInfo.CurrentlyInstalling.Count} installing, {queueInfo.Queued} queued, {queueInfo.Paused} paused";

                // Update download items
                var currentGameIds = new HashSet<Guid>(downloadItems.Select(d => d.GameId));
                var newGameIds = new HashSet<Guid>(allJobs.Select(j => j.Game.Id));

                // Remove items that no longer exist
                var toRemove = downloadItems.Where(d => !newGameIds.Contains(d.GameId)).ToList();
                foreach (var item in toRemove)
                {
                    downloadItems.Remove(item);
                }

                // Update or add items
                foreach (var job in allJobs)
                {
                    var existingItem = downloadItems.FirstOrDefault(d => d.GameId == job.Game.Id);
                    if (existingItem != null)
                    {
                        // Update existing item
                        existingItem.UpdateFromJob(job);
                    }
                    else
                    {
                        // Add new item
                        downloadItems.Add(new DownloadItemViewModel(job));
                    }
                }

                // Sort: InProgress first, then Pending, then Paused, then others
                var sorted = downloadItems.OrderBy(d =>
                {
                    switch (d.StatusText)
                    {
                        case "Installing": return 0;
                        case "Queued": return 1;
                        case "Paused": return 2;
                        default: return 3;
                    }
                }).ToList();

                downloadItems.Clear();
                foreach (var item in sorted)
                {
                    downloadItems.Add(item);
                }
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                System.Diagnostics.Debug.WriteLine($"Error refreshing downloads: {ex.Message}");
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is Guid gameId)
            {
                var instance = BackgroundInstallManager.Instance;
                if (instance != null)
                {
                    instance.PauseInstallation(gameId);
                    RefreshDownloads();
                }
            }
        }

        private void ResumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is Guid gameId)
            {
                var instance = BackgroundInstallManager.Instance;
                if (instance != null)
                {
                    instance.ResumeInstallation(gameId);
                    RefreshDownloads();
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is Guid gameId)
            {
                var result = MessageBox.Show(
                    "Are you sure you want to cancel this installation?",
                    "Cancel Installation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    var instance = BackgroundInstallManager.Instance;
                    if (instance != null)
                    {
                        instance.CancelInstallation(gameId);
                        RefreshDownloads();
                    }
                }
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshDownloads();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void PriorityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && comboBox.Tag is Guid gameId)
            {
                var instance = BackgroundInstallManager.Instance;
                if (instance != null && comboBox.SelectedIndex >= 0)
                {
                    InstallationPriority priority = (InstallationPriority)comboBox.SelectedIndex;
                    instance.SetJobPriority(gameId, priority);
                    RefreshDownloads();
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            refreshTimer?.Stop();
            base.OnClosed(e);
        }
    }

    public class DownloadItemViewModel
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string PriorityText { get; set; }
        public int PriorityIndex { get; set; }
        public string StatusText { get; set; }
        public string ProgressText { get; set; }
        public string SpeedText { get; set; }
        public bool CanPause { get; set; }
        public bool CanResume { get; set; }
        public bool CanCancel { get; set; }

        public DownloadItemViewModel(InstallationJob job)
        {
            UpdateFromJob(job);
        }

        public void UpdateFromJob(InstallationJob job)
        {
            GameId = job.Game.Id;
            GameName = job.Game.Name;

            // Set priority text and index
            switch (job.Priority)
            {
                case InstallationPriority.Low:
                    PriorityText = "Low";
                    PriorityIndex = 0;
                    break;
                case InstallationPriority.Normal:
                    PriorityText = "Normal";
                    PriorityIndex = 1;
                    break;
                case InstallationPriority.High:
                    PriorityText = "High";
                    PriorityIndex = 2;
                    break;
                default:
                    PriorityText = "Normal";
                    PriorityIndex = 1;
                    break;
            }

            switch (job.Status)
            {
                case InstallationStatus.Pending:
                    StatusText = "Queued";
                    break;
                case InstallationStatus.InProgress:
                    StatusText = "Installing";
                    break;
                case InstallationStatus.Paused:
                    StatusText = "Paused";
                    break;
                case InstallationStatus.Completed:
                    StatusText = "Completed";
                    break;
                case InstallationStatus.Failed:
                    StatusText = "Failed";
                    break;
                case InstallationStatus.Cancelled:
                    StatusText = "Cancelled";
                    break;
                default:
                    StatusText = "Unknown";
                    break;
            }

            // Update progress if available from progress window
            // Note: We can't directly access UI elements from another thread, so we'll show status-based info
            ProgressText = job.Status == InstallationStatus.InProgress ? "In Progress" : "--";
            SpeedText = job.Status == InstallationStatus.InProgress ? "Active" : "--";

            CanPause = job.Status == InstallationStatus.InProgress;
            CanResume = job.Status == InstallationStatus.Paused;
            CanCancel = job.Status == InstallationStatus.Pending || 
                       job.Status == InstallationStatus.InProgress || 
                       job.Status == InstallationStatus.Paused;
        }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
