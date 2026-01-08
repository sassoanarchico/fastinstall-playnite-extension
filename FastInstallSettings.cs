using System;
using System.Collections.Generic;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace FastInstall
{
    public class FastInstallSettings : ObservableObject
    {
        private string sourceArchiveDirectory = string.Empty;
        private string fastInstallDirectory = string.Empty;

        /// <summary>
        /// Path to the slow archival HDD where games are stored
        /// </summary>
        public string SourceArchiveDirectory
        {
            get => sourceArchiveDirectory;
            set => SetValue(ref sourceArchiveDirectory, value);
        }

        /// <summary>
        /// Path to the fast SSD where games will be installed/copied to
        /// </summary>
        public string FastInstallDirectory
        {
            get => fastInstallDirectory;
            set => SetValue(ref fastInstallDirectory, value);
        }
    }

    public class FastInstallSettingsViewModel : ObservableObject, ISettings
    {
        private readonly FastInstallPlugin plugin;
        private FastInstallSettings editingClone;
        private FastInstallSettings settings;

        public FastInstallSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public FastInstallSettingsViewModel(FastInstallPlugin plugin)
        {
            this.plugin = plugin;

            // Load saved settings or create new ones
            var savedSettings = plugin.LoadPluginSettings<FastInstallSettings>();
            Settings = savedSettings ?? new FastInstallSettings();
        }

        public void BeginEdit()
        {
            // Create a copy for editing so we can cancel changes
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            // Restore original values
            Settings = editingClone;
        }

        public void EndEdit()
        {
            // Save settings
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Settings.SourceArchiveDirectory))
            {
                errors.Add("Source Archive Directory is required.");
            }
            else if (!System.IO.Directory.Exists(Settings.SourceArchiveDirectory))
            {
                errors.Add($"Source Archive Directory does not exist: {Settings.SourceArchiveDirectory}");
            }

            if (string.IsNullOrWhiteSpace(Settings.FastInstallDirectory))
            {
                errors.Add("Fast Install Directory is required.");
            }

            return errors.Count == 0;
        }

        // Commands for folder picker buttons
        public RelayCommand<object> BrowseSourceArchiveCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                var selectedFolder = plugin.PlayniteApi.Dialogs.SelectFolder();
                if (!string.IsNullOrEmpty(selectedFolder))
                {
                    Settings.SourceArchiveDirectory = selectedFolder;
                }
            });
        }

        public RelayCommand<object> BrowseFastInstallCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                var selectedFolder = plugin.PlayniteApi.Dialogs.SelectFolder();
                if (!string.IsNullOrEmpty(selectedFolder))
                {
                    Settings.FastInstallDirectory = selectedFolder;
                }
            });
        }
    }
}

