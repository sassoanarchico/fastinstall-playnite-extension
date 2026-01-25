using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Reflection;
using Playnite.SDK;

namespace FastInstall
{
    public partial class FastInstallSettingsView : UserControl
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private FastInstallPlugin plugin;
        private ResourceDictionary loadedLocalizationResources;

        public FastInstallSettingsView(FastInstallPlugin plugin = null)
        {
            this.plugin = plugin;
            InitializeComponent();
            LoadLocalizationResources();
            Loaded += FastInstallSettingsView_Loaded;
        }

        private void LoadLocalizationResources()
        {
            try
            {
                // Determine language code from current culture
                var culture = System.Globalization.CultureInfo.CurrentCulture;
                var langCode = culture.Name.ToLowerInvariant().Replace("-", "_");
                
                // Map common language codes to our file names
                if (langCode.StartsWith("it"))
                    langCode = "it_it";
                else
                    langCode = "en_us";

                var possiblePaths = new System.Collections.Generic.List<string>();

                // Try 1: Get extension directory from assembly location (most reliable)
                // When installed, Playnite extracts extensions to a specific directory
                // The assembly location should point to the extension directory

                // Try 2: Assembly location (when running from development or installed)
                var assembly = Assembly.GetExecutingAssembly();
                var assemblyLocation = Path.GetDirectoryName(assembly.Location);
                if (!string.IsNullOrWhiteSpace(assemblyLocation))
                {
                    possiblePaths.Add(Path.Combine(assemblyLocation, "Localization", $"{langCode}.xaml"));
                    possiblePaths.Add(Path.Combine(assemblyLocation, $"{langCode}.xaml"));
                }

                // Try 3: AppDomain base directory
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    possiblePaths.Add(Path.Combine(baseDir, "Localization", $"{langCode}.xaml"));
                }

                // Try 4: Common Playnite extension directories
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var playniteExtensionsPath = Path.Combine(localAppData, "Playnite", "Extensions");
                if (Directory.Exists(playniteExtensionsPath))
                {
                    // Look for FastInstall in Libraries subdirectory
                    var librariesPath = Path.Combine(playniteExtensionsPath, "Libraries");
                    if (Directory.Exists(librariesPath))
                    {
                        var fastInstallDirs = Directory.GetDirectories(librariesPath, "*FastInstall*", SearchOption.TopDirectoryOnly);
                        foreach (var dir in fastInstallDirs)
                        {
                            possiblePaths.Add(Path.Combine(dir, "Localization", $"{langCode}.xaml"));
                        }
                    }
                }

                ResourceDictionary loadedDict = null;
                string loadedPath = null;

                foreach (var localizationPath in possiblePaths)
                {
                    if (File.Exists(localizationPath))
                    {
                        try
                        {
                            using (var stream = File.OpenRead(localizationPath))
                            {
                                var resourceDict = XamlReader.Load(stream) as ResourceDictionary;
                                if (resourceDict != null)
                                {
                                    loadedDict = resourceDict;
                                    loadedPath = localizationPath;
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Warn(ex, $"FastInstall: Failed to load localization file {localizationPath}");
                        }
                    }
                }

                if (loadedDict != null)
                {
                    // Store reference for fallback access
                    loadedLocalizationResources = loadedDict;

                    // Merge the localization resources into the UserControl's resources
                    Resources.MergedDictionaries.Add(loadedDict);
                    logger.Info($"FastInstall: Loaded localization resources from {loadedPath}");
                    
                    // Also try to add to Application.Resources if available
                    try
                    {
                        if (Application.Current != null && Application.Current.Resources != null)
                        {
                            // Check if already added to avoid duplicates
                            bool alreadyAdded = false;
                            foreach (var dict in Application.Current.Resources.MergedDictionaries)
                            {
                                if (dict.Source != null && dict.Source.ToString().Contains("FastInstall"))
                                {
                                    alreadyAdded = true;
                                    break;
                                }
                            }
                            
                            if (!alreadyAdded)
                            {
                                Application.Current.Resources.MergedDictionaries.Add(loadedDict);
                                logger.Info("FastInstall: Also added localization resources to Application.Resources");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex, "FastInstall: Could not add resources to Application.Resources");
                    }
                }
                else
                {
                    logger.Warn($"FastInstall: Localization file not found in any of the checked paths. Searched {possiblePaths.Count} paths.");
                    logger.Debug($"FastInstall: Searched paths: {string.Join("; ", possiblePaths.Take(5))}...");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FastInstall: Error loading localization resources");
            }
        }

        private void FastInstallSettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            // Populate ConflictResolution ComboBox
            if (ConflictResolutionComboBox != null)
            {
                ConflictResolutionComboBox.ItemsSource = Enum.GetValues(typeof(ConflictResolution)).Cast<ConflictResolution>();
            }
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

        private void BrowseSevenZipButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is FastInstallSettingsViewModel viewModel)
            {
                if (viewModel.plugin?.PlayniteApi != null)
                {
                    var filter = ResourceProvider.GetString("LOCFastInstall_Settings_Select7Zip_Filter");
                    var selectedFile = viewModel.plugin.PlayniteApi.Dialogs.SelectFile(filter);
                    if (!string.IsNullOrEmpty(selectedFile) && File.Exists(selectedFile))
                    {
                        viewModel.Settings.SevenZipPath = selectedFile;
                    }
                }
            }
        }

        private void DownloadSevenZipButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Open 7-Zip download page in default browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.7-zip.org/download.html",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FastInstall: Error opening 7-Zip download page");
                if (DataContext is FastInstallSettingsViewModel viewModel)
                {
                    var msg = string.Format(
                        ResourceProvider.GetString("LOCFastInstall_Settings_ErrorOpenBrowser_Format"),
                        ex.Message);
                    viewModel.plugin?.PlayniteApi?.Dialogs.ShowErrorMessage(
                        msg,
                        ResourceProvider.GetString("LOCFastInstall_Settings_Download7Zip_Title"));
                }
            }
        }

        private void ShowApiKeyHelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is FastInstallSettingsViewModel viewModel)
            {
                var message = ResourceProvider.GetString("LOCFastInstall_Settings_ApiKeyHelp_Message")
                            + "\n\n"
                            + ResourceProvider.GetString("LOCFastInstall_Settings_ApiKeyHelp_ConfirmOpenConsole");

                var result = viewModel.plugin?.PlayniteApi?.Dialogs.ShowMessage(
                    message,
                    ResourceProvider.GetString("LOCFastInstall_Settings_ApiKeyHelp_Title"),
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information);

                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://console.cloud.google.com/apis/credentials",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "FastInstall: Error opening Google Cloud Console");
                    }
                }
            }
        }
    }
}

