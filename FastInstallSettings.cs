using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;

namespace FastInstall
{
    /// <summary>
    /// Configuration for a single source/destination pair
    /// </summary>
    public class FolderConfiguration : ObservableObject
    {
        private bool isEnabled = true;
        private string sourcePath = string.Empty;
        private string destinationPath = string.Empty;
        private string platform = "PC";
        private Guid? emulatorId = null;
        private string emulatorProfileId = null;

        public bool IsEnabled
        {
            get => isEnabled;
            set => SetValue(ref isEnabled, value);
        }

        public string SourcePath
        {
            get => sourcePath;
            set => SetValue(ref sourcePath, value);
        }

        public string DestinationPath
        {
            get => destinationPath;
            set => SetValue(ref destinationPath, value);
        }

        /// <summary>
        /// Platform: PC, PS3, Switch, Xbox 360, etc.
        /// </summary>
        public string Platform
        {
            get => platform;
            set => SetValue(ref platform, value);
        }

        /// <summary>
        /// ID of the selected emulator from Playnite
        /// </summary>
        public Guid? EmulatorId
        {
            get => emulatorId;
            set => SetValue(ref emulatorId, value);
        }

        /// <summary>
        /// ID of the selected emulator profile
        /// </summary>
        public string EmulatorProfileId
        {
            get => emulatorProfileId;
            set => SetValue(ref emulatorProfileId, value);
        }

        /// <summary>
        /// Helper property for binding in UI - stores the emulator selection as a combined string
        /// </summary>
        [DontSerialize]
        public string EmulatorSelectionKey
        {
            get
            {
                if (!EmulatorId.HasValue || EmulatorId.Value == Guid.Empty)
                    return string.Empty;
                
                return EmulatorProfileId != null 
                    ? $"{EmulatorId}|{EmulatorProfileId}" 
                    : EmulatorId.ToString();
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    EmulatorId = null;
                    EmulatorProfileId = null;
                }
                else
                {
                    var parts = value.Split('|');
                    if (Guid.TryParse(parts[0], out var guid))
                    {
                        EmulatorId = guid;
                        EmulatorProfileId = parts.Length > 1 ? parts[1] : null;
                    }
                }
                OnPropertyChanged();
            }
        }

        // Legacy property - kept for backward compatibility with old settings
        [DontSerialize]
        public string EmulatorPath { get; set; }
    }

    public class FastInstallSettings : ObservableObject
    {
        private ObservableCollection<FolderConfiguration> folderConfigurations = new ObservableCollection<FolderConfiguration>();

        // Legacy settings - kept for backward compatibility
        private string sourceArchiveDirectory = string.Empty;
        private string fastInstallDirectory = string.Empty;
        private string rpcs3Path = string.Empty;

        public ObservableCollection<FolderConfiguration> FolderConfigurations
        {
            get => folderConfigurations;
            set => SetValue(ref folderConfigurations, value);
        }

        // Legacy properties - kept for backward compatibility
        [DontSerialize]
        public string SourceArchiveDirectory
        {
            get => sourceArchiveDirectory;
            set => SetValue(ref sourceArchiveDirectory, value);
        }

        [DontSerialize]
        public string FastInstallDirectory
        {
            get => fastInstallDirectory;
            set => SetValue(ref fastInstallDirectory, value);
        }

        [DontSerialize]
        public string Rpcs3Path
        {
            get => rpcs3Path;
            set => SetValue(ref rpcs3Path, value);
        }

        /// <summary>
        /// Migrates legacy settings to new configuration format
        /// </summary>
        public void MigrateLegacySettings()
        {
            if (!string.IsNullOrWhiteSpace(sourceArchiveDirectory) && 
                !string.IsNullOrWhiteSpace(fastInstallDirectory) &&
                FolderConfigurations.Count == 0)
            {
                FolderConfigurations.Add(new FolderConfiguration
                {
                    IsEnabled = true,
                    SourcePath = sourceArchiveDirectory,
                    DestinationPath = fastInstallDirectory,
                    Platform = "PC",
                    EmulatorPath = rpcs3Path ?? string.Empty
                });

                sourceArchiveDirectory = string.Empty;
                fastInstallDirectory = string.Empty;
                rpcs3Path = string.Empty;
            }
        }
    }

    /// <summary>
    /// Helper class for displaying emulator profiles in ComboBox
    /// </summary>
    public class EmulatorProfileItem : ObservableObject
    {
        public Guid EmulatorId { get; set; }
        public string ProfileId { get; set; }
        public string DisplayName { get; set; }
        public Emulator Emulator { get; set; }
        public EmulatorProfile Profile { get; set; }

        /// <summary>
        /// Unique key for this emulator/profile combination
        /// </summary>
        public string SelectionKey
        {
            get
            {
                if (EmulatorId == Guid.Empty)
                    return string.Empty;
                
                return ProfileId != null 
                    ? $"{EmulatorId}|{ProfileId}" 
                    : EmulatorId.ToString();
            }
        }

        public override string ToString() => DisplayName;
    }

    public class FastInstallSettingsViewModel : ObservableObject, ISettings
    {
        private readonly FastInstallPlugin plugin;
        private FastInstallSettings editingClone;
        private FastInstallSettings settings;
        private ObservableCollection<FolderConfiguration> editingConfigurations;
        private FolderConfiguration selectedConfiguration;

        // Available platforms for dropdown
        private readonly List<string> availablePlatforms = new List<string>
        {
            "PC",
            "Sony PlayStation 3",
            "Sony PlayStation 2", 
            "Sony PSP",
            "Nintendo Switch",
            "Nintendo Wii U",
            "Nintendo Wii",
            "Nintendo GameCube",
            "Microsoft Xbox 360"
        };

        // Available emulators from Playnite
        private ObservableCollection<EmulatorProfileItem> availableEmulators;

        // Cached commands
        private RelayCommand<object> addConfigurationCommand;
        private RelayCommand<object> removeConfigurationCommand;
        private RelayCommand<FolderConfiguration> browseSourceCommand;
        private RelayCommand<FolderConfiguration> browseDestinationCommand;

        public FastInstallSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<FolderConfiguration> EditingConfigurations
        {
            get => editingConfigurations;
            set
            {
                editingConfigurations = value;
                OnPropertyChanged();
            }
        }

        public FolderConfiguration SelectedConfiguration
        {
            get => selectedConfiguration;
            set
            {
                selectedConfiguration = value;
                OnPropertyChanged();
            }
        }

        public List<string> AvailablePlatforms => availablePlatforms;

        public ObservableCollection<EmulatorProfileItem> AvailableEmulators
        {
            get
            {
                if (availableEmulators == null)
                {
                    LoadAvailableEmulators();
                }
                return availableEmulators;
            }
        }

        public FastInstallSettingsViewModel(FastInstallPlugin plugin)
        {
            this.plugin = plugin;

            // Load saved settings or create new ones
            var savedSettings = plugin.LoadPluginSettings<FastInstallSettings>();
            Settings = savedSettings ?? new FastInstallSettings();

            // Migrate legacy settings if needed
            Settings.MigrateLegacySettings();

            // Initialize with empty configuration if none exist
            if (Settings.FolderConfigurations.Count == 0)
            {
                Settings.FolderConfigurations.Add(new FolderConfiguration
                {
                    IsEnabled = true,
                    Platform = "PC"
                });
            }
        }

        private void LoadAvailableEmulators()
        {
            availableEmulators = new ObservableCollection<EmulatorProfileItem>();

            // Add "None" option
            availableEmulators.Add(new EmulatorProfileItem
            {
                EmulatorId = Guid.Empty,
                ProfileId = null,
                DisplayName = "(None / Auto-detect)"
            });

            try
            {
                // Get all emulators from Playnite
                var emulators = plugin.PlayniteApi.Database.Emulators;

                foreach (var emulator in emulators.OrderBy(e => e.Name))
                {
                    // Just add the emulator, without trying to access Profiles directly
                    availableEmulators.Add(new EmulatorProfileItem
                    {
                        EmulatorId = emulator.Id,
                        ProfileId = null,
                        DisplayName = emulator.Name,
                        Emulator = emulator,
                        Profile = null
                    });
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Error(ex, "FastInstall: Error loading emulators from Playnite");
            }

            OnPropertyChanged(nameof(AvailableEmulators));
        }

        public void BeginEdit()
        {
            // Create a deep copy for editing
            editingClone = Serialization.GetClone(Settings);
            EditingConfigurations = new ObservableCollection<FolderConfiguration>(
                Settings.FolderConfigurations.Select(c => Serialization.GetClone(c))
            );

            // Refresh emulators list in case user added/removed emulators
            LoadAvailableEmulators();
        }

        public void CancelEdit()
        {
            // Restore original values
            Settings = editingClone;
            EditingConfigurations = null;
        }

        public void EndEdit()
        {
            // Copy editing configurations back to settings
            if (EditingConfigurations != null)
            {
                Settings.FolderConfigurations.Clear();
                foreach (var config in EditingConfigurations)
                {
                    Settings.FolderConfigurations.Add(config);
                }
            }

            // Save settings
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (Settings.FolderConfigurations.Count == 0)
            {
                errors.Add("At least one folder configuration is required.");
                return false;
            }

            var enabledConfigs = Settings.FolderConfigurations.Where(c => c.IsEnabled).ToList();
            if (enabledConfigs.Count == 0)
            {
                errors.Add("At least one folder configuration must be enabled.");
                return false;
            }

            foreach (var config in enabledConfigs)
            {
                if (string.IsNullOrWhiteSpace(config.SourcePath))
                {
                    errors.Add("All enabled configurations must have a source path.");
                }
                else if (!System.IO.Directory.Exists(config.SourcePath))
                {
                    errors.Add($"Source directory does not exist: {config.SourcePath}");
                }

                if (string.IsNullOrWhiteSpace(config.DestinationPath))
                {
                    errors.Add("All enabled configurations must have a destination path.");
                }
            }

            return errors.Count == 0;
        }

        // Commands
        public RelayCommand<object> AddConfigurationCommand
        {
            get
            {
                if (addConfigurationCommand == null)
                {
                    addConfigurationCommand = new RelayCommand<object>((a) =>
                    {
                        var newConfig = new FolderConfiguration
                        {
                            IsEnabled = true,
                            Platform = "PC"
                        };
                        EditingConfigurations.Add(newConfig);
                        SelectedConfiguration = newConfig;
                    });
                }
                return addConfigurationCommand;
            }
        }

        public RelayCommand<object> RemoveConfigurationCommand
        {
            get
            {
                if (removeConfigurationCommand == null)
                {
                    removeConfigurationCommand = new RelayCommand<object>(
                        (a) =>
                        {
                            if (SelectedConfiguration != null)
                            {
                                EditingConfigurations.Remove(SelectedConfiguration);
                            }
                        },
                        (a) => SelectedConfiguration != null && EditingConfigurations?.Count > 1
                    );
                }
                return removeConfigurationCommand;
            }
        }

        public RelayCommand<FolderConfiguration> BrowseSourceCommand
        {
            get
            {
                if (browseSourceCommand == null)
                {
                    browseSourceCommand = new RelayCommand<FolderConfiguration>((config) =>
                    {
                        var selectedFolder = plugin.PlayniteApi.Dialogs.SelectFolder();
                        if (!string.IsNullOrEmpty(selectedFolder) && config != null)
                        {
                            config.SourcePath = selectedFolder;
                        }
                    });
                }
                return browseSourceCommand;
            }
        }

        public RelayCommand<FolderConfiguration> BrowseDestinationCommand
        {
            get
            {
                if (browseDestinationCommand == null)
                {
                    browseDestinationCommand = new RelayCommand<FolderConfiguration>((config) =>
                    {
                        var selectedFolder = plugin.PlayniteApi.Dialogs.SelectFolder();
                        if (!string.IsNullOrEmpty(selectedFolder) && config != null)
                        {
                            config.DestinationPath = selectedFolder;
                        }
                    });
                }
                return browseDestinationCommand;
            }
        }
    }
}
