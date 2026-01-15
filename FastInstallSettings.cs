using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
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
        private ObservableCollection<EmulatorProfileItem> cachedProfilesForEmulator = null;

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
            set
            {
                var oldValue = emulatorId;
                SetValue(ref emulatorId, value);
                
                // When emulator changes, clear profile selection and update available profiles
                if (oldValue != value)
                {
                    // Invalidate cached profiles
                    cachedProfilesForEmulator = null;
                    
                    // Clear profile if emulator is deselected or changed
                    if (!value.HasValue || value.Value == Guid.Empty)
                    {
                        EmulatorProfileId = null;
                    }
                    else if (emulatorProfileId != null)
                    {
                        // Check if current profile belongs to the new emulator
                        UpdateCachedProfiles();
                        var currentProfileExists = cachedProfilesForEmulator?.Any(p => p.ProfileId == emulatorProfileId) ?? false;
                        if (!currentProfileExists)
                        {
                            EmulatorProfileId = null;
                        }
                    }
                    
                    OnPropertyChanged(nameof(AvailableProfilesForEmulator));
                }
            }
        }

        /// <summary>
        /// Reference to the ViewModel to access available profiles
        /// This is set by the ViewModel when creating configurations
        /// </summary>
        [DontSerialize]
        public FastInstallSettingsViewModel ViewModel { get; set; }

        /// <summary>
        /// Updates the cached profiles list for the current emulator
        /// </summary>
        private void UpdateCachedProfiles()
        {
            if (ViewModel == null || !EmulatorId.HasValue || EmulatorId.Value == Guid.Empty)
            {
                cachedProfilesForEmulator = new ObservableCollection<EmulatorProfileItem>();
                return;
            }

            var allProfiles = ViewModel.AvailableProfiles;
            cachedProfilesForEmulator = new ObservableCollection<EmulatorProfileItem>(
                allProfiles.Where(p => p.EmulatorId == EmulatorId.Value)
            );

            // Add a "None" option at the beginning
            cachedProfilesForEmulator.Insert(0, new EmulatorProfileItem
            {
                EmulatorId = EmulatorId.Value,
                ProfileId = null,
                DisplayName = "(Default / Auto)"
            });
        }

        /// <summary>
        /// Returns only the profiles for the currently selected emulator
        /// Uses a cached collection that updates when EmulatorId changes
        /// </summary>
        [DontSerialize]
        public ObservableCollection<EmulatorProfileItem> AvailableProfilesForEmulator
        {
            get
            {
                if (cachedProfilesForEmulator == null)
                {
                    UpdateCachedProfiles();
                }
                return cachedProfilesForEmulator ?? new ObservableCollection<EmulatorProfileItem>();
            }
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

    /// <summary>
    /// How to handle conflicts when destination already exists
    /// </summary>
    public enum ConflictResolution
    {
        Ask,        // Ask user each time
        Overwrite,  // Always overwrite
        Skip        // Skip installation if exists
    }

    public class FastInstallSettings : ObservableObject
    {
        private ObservableCollection<FolderConfiguration> folderConfigurations = new ObservableCollection<FolderConfiguration>();
        private bool enableParallelDownloads = false;
        private int maxParallelDownloads = 2;
        private ConflictResolution conflictResolution = ConflictResolution.Ask;
        private string sevenZipPath = string.Empty;

        // Legacy settings - kept for backward compatibility
        private string sourceArchiveDirectory = string.Empty;
        private string fastInstallDirectory = string.Empty;
        private string rpcs3Path = string.Empty;

        public ObservableCollection<FolderConfiguration> FolderConfigurations
        {
            get => folderConfigurations;
            set => SetValue(ref folderConfigurations, value);
        }

        /// <summary>
        /// Enable parallel downloads (if false, downloads are sequential)
        /// </summary>
        public bool EnableParallelDownloads
        {
            get => enableParallelDownloads;
            set
            {
                SetValue(ref enableParallelDownloads, value);
                // When disabled, ensure maxParallelDownloads is effectively 1
                if (!value)
                {
                    OnPropertyChanged(nameof(EffectiveMaxParallelDownloads));
                }
            }
        }

        /// <summary>
        /// Maximum number of parallel downloads (only used when EnableParallelDownloads is true)
        /// </summary>
        public int MaxParallelDownloads
        {
            get => maxParallelDownloads;
            set
            {
                if (value < 1) value = 1;
                if (value > 10) value = 10; // Reasonable limit
                SetValue(ref maxParallelDownloads, value);
                OnPropertyChanged(nameof(EffectiveMaxParallelDownloads));
            }
        }

        /// <summary>
        /// Effective maximum parallel downloads (1 if disabled, MaxParallelDownloads if enabled)
        /// </summary>
        [DontSerialize]
        public int EffectiveMaxParallelDownloads
        {
            get => enableParallelDownloads ? maxParallelDownloads : 1;
        }

        /// <summary>
        /// How to handle conflicts when destination directory already exists
        /// </summary>
        public ConflictResolution ConflictResolution
        {
            get => conflictResolution;
            set => SetValue(ref conflictResolution, value);
        }

        /// <summary>
        /// Path to 7-Zip executable (7z.exe or 7za.exe)
        /// Required for extracting ZIP, RAR, and 7Z archives
        /// </summary>
        public string SevenZipPath
        {
            get => sevenZipPath;
            set => SetValue(ref sevenZipPath, value);
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
        internal readonly FastInstallPlugin plugin;
        private FastInstallSettings editingClone;
        private FastInstallSettings settings;
        private ObservableCollection<FolderConfiguration> editingConfigurations;
        private FolderConfiguration selectedConfiguration;

        /// <summary>
        /// Version shown in the Settings UI.
        /// Uses the built assembly version to stay in sync with the real plugin binary.
        /// </summary>
        public string PluginVersionDisplay
        {
            get
            {
                try
                {
                    var asmVersion = plugin?.GetType()?.Assembly?.GetName()?.Version;
                    if (asmVersion != null)
                    {
                        // Show Major.Minor.Build (e.g. 0.1.5)
                        return asmVersion.ToString(3);
                    }
                }
                catch
                {
                    // Ignore and fall back
                }

                return FastInstallPlugin.PluginVersion;
            }
        }

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

        // Available emulators from Playnite (emulator dropdown)
        private ObservableCollection<EmulatorProfileItem> availableEmulators;

        // Available emulator profiles from Playnite (profile dropdown)
        private ObservableCollection<EmulatorProfileItem> availableProfiles;

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

        public ObservableCollection<EmulatorProfileItem> AvailableProfiles
        {
            get
            {
                if (availableProfiles == null)
                {
                    LoadAvailableEmulators();
                }
                return availableProfiles;
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

            // Ensure UI can display version
            OnPropertyChanged(nameof(PluginVersionDisplay));
        }

        private void LoadAvailableEmulators()
        {
            availableEmulators = new ObservableCollection<EmulatorProfileItem>();
            availableProfiles = new ObservableCollection<EmulatorProfileItem>();

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
                    // Add emulator (no specific profile)
                    availableEmulators.Add(new EmulatorProfileItem
                    {
                        EmulatorId = emulator.Id,
                        ProfileId = null,
                        DisplayName = emulator.Name,
                        Emulator = emulator,
                        Profile = null
                    });

                    // Add each profile for this emulator (using reflection to safely access SelectableProfiles property)
                    try
                    {
                        // Prefer the official SelectableProfiles API if available
                        var profilesProperty = emulator.GetType().GetProperty("SelectableProfiles")
                                              ?? emulator.GetType().GetProperty("Profiles");
                        if (profilesProperty != null)
                        {
                            var profiles = profilesProperty.GetValue(emulator) as IEnumerable;
                            if (profiles != null)
                            {
                                foreach (var profileObj in profiles)
                                {
                                    var profile = profileObj as EmulatorProfile;
                                    if (profile != null)
                                    {
                                        var nameProperty = profile.GetType().GetProperty("Name");
                                        var idProperty = profile.GetType().GetProperty("Id");
                                        
                                        var profileName = nameProperty?.GetValue(profile)?.ToString() ?? "Unknown";
                                        var profileId = idProperty?.GetValue(profile)?.ToString();

                                        if (!string.IsNullOrWhiteSpace(profileId))
                                        {
                                            availableProfiles.Add(new EmulatorProfileItem
                                            {
                                                EmulatorId = emulator.Id,
                                                ProfileId = profileId,
                                                DisplayName = $"{emulator.Name} - {profileName}",
                                                Emulator = emulator,
                                                Profile = profile
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception profileEx)
                    {
                        // Profiles not available in this Playnite SDK version - skip
                        LogManager.GetLogger().Debug(profileEx, $"FastInstall: Could not load profiles for emulator '{emulator.Name}'");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Error(ex, "FastInstall: Error loading emulators from Playnite");
            }

            OnPropertyChanged(nameof(AvailableEmulators));
            OnPropertyChanged(nameof(AvailableProfiles));
        }

        public void BeginEdit()
        {
            // Create a deep copy for editing
            editingClone = Serialization.GetClone(Settings);
            EditingConfigurations = new ObservableCollection<FolderConfiguration>(
                Settings.FolderConfigurations.Select(c => Serialization.GetClone(c))
            );

            // Set ViewModel reference on each configuration so they can filter profiles
            foreach (var config in EditingConfigurations)
            {
                config.ViewModel = this;
            }

            // Refresh emulators list in case user added/removed emulators
            LoadAvailableEmulators();
            
            // Apply max parallel downloads setting
            BackgroundInstallManager.Instance?.SetMaxParallelInstalls(Settings.EffectiveMaxParallelDownloads);
            
            // Update 7-Zip path getter
            BackgroundInstallManager.Instance?.SetSevenZipPathGetter(() => Settings.SevenZipPath ?? string.Empty);
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

            // Apply max parallel downloads setting
            BackgroundInstallManager.Instance?.SetMaxParallelInstalls(Settings.EffectiveMaxParallelDownloads);
            
            // Update 7-Zip path getter
            BackgroundInstallManager.Instance?.SetSevenZipPathGetter(() => Settings.SevenZipPath ?? string.Empty);

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
                            Platform = "PC",
                            ViewModel = this
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
