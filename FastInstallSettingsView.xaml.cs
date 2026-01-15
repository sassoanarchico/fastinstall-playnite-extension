using System.Windows.Controls;
using System.Windows;

namespace FastInstall
{
    public partial class FastInstallSettingsView : UserControl
    {
        public FastInstallSettingsView()
        {
            InitializeComponent();
        }

        private void EnableParallelCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (DataContext is FastInstallSettingsViewModel viewModel)
            {
                viewModel.Settings.EnableParallelDownloads = true;
                ApplyParallelDownloadsSetting(viewModel);
            }
        }

        private void EnableParallelCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (DataContext is FastInstallSettingsViewModel viewModel)
            {
                viewModel.Settings.EnableParallelDownloads = false;
                ApplyParallelDownloadsSetting(viewModel);
            }
        }

        private void MaxParallelTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // Only allow numeric input
            e.Handled = !System.Text.RegularExpressions.Regex.IsMatch(e.Text, "^[0-9]+$");
        }

        private void MaxParallelTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox && DataContext is FastInstallSettingsViewModel viewModel)
            {
                if (int.TryParse(textBox.Text, out int value))
                {
                    // Clamp value between 1 and 10
                    if (value < 1) value = 1;
                    if (value > 10) value = 10;
                    viewModel.Settings.MaxParallelDownloads = value;
                    textBox.Text = value.ToString();
                    ApplyParallelDownloadsSetting(viewModel);
                }
                else
                {
                    // Reset to current value if invalid
                    textBox.Text = viewModel.Settings.MaxParallelDownloads.ToString();
                }
            }
        }

        private void ApplyParallelDownloadsSetting(FastInstallSettingsViewModel viewModel)
        {
            try
            {
                var effectiveMax = viewModel.Settings.EffectiveMaxParallelDownloads;
                var instance = BackgroundInstallManager.Instance;
                if (instance != null)
                {
                    instance.SetMaxParallelInstalls(effectiveMax);
                }
            }
            catch
            {
                // BackgroundInstallManager not initialized yet - settings will be applied when plugin loads
                // This is safe to ignore as the setting will be applied in BeginEdit/EndEdit
            }
        }

        private void OpenDownloadManagerButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new DownloadManagerWindow();
            window.Show();
        }
    }
}

