using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace FastInstall
{
    public class FastInstallPlugin : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private FastInstallSettingsViewModel settingsViewModel;

        public override Guid Id { get; } = Guid.Parse("F8A1B2C3-D4E5-6789-ABCD-EF1234567890");
        public override string Name => "FastInstall";
        // Optional: Add an icon.png to the project folder for a custom library icon
        // public override string LibraryIcon => Path.Combine(Path.GetDirectoryName(typeof(FastInstallPlugin).Assembly.Location), "icon.png");

        public FastInstallSettings Settings => settingsViewModel?.Settings;

        public FastInstallPlugin(IPlayniteAPI api) : base(api)
        {
            settingsViewModel = new FastInstallSettingsViewModel(this);
            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };
        }

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var games = new List<GameMetadata>();

            if (string.IsNullOrWhiteSpace(Settings?.SourceArchiveDirectory))
            {
                logger.Warn("FastInstall: Source Archive Directory is not configured.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "FastInstall_NoSource",
                    "FastInstall: Please configure the Source Archive Directory in settings.",
                    NotificationType.Error,
                    () => PlayniteApi.MainView.OpenPluginSettings(Id)));
                return games;
            }

            if (!Directory.Exists(Settings.SourceArchiveDirectory))
            {
                logger.Error($"FastInstall: Source Archive Directory does not exist: {Settings.SourceArchiveDirectory}");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "FastInstall_SourceNotFound",
                    $"FastInstall: Source Archive Directory not found: {Settings.SourceArchiveDirectory}",
                    NotificationType.Error));
                return games;
            }

            try
            {
                var directories = Directory.GetDirectories(Settings.SourceArchiveDirectory);
                logger.Info($"FastInstall: Found {directories.Length} game folders in archive.");

                foreach (var dir in directories)
                {
                    var gameName = Path.GetFileName(dir);
                    var gameId = GenerateGameId(gameName);

                    // Check if game is already installed on SSD
                    bool isInstalled = false;
                    string installDir = null;

                    if (!string.IsNullOrWhiteSpace(Settings.FastInstallDirectory))
                    {
                        var potentialInstallPath = Path.Combine(Settings.FastInstallDirectory, gameName);
                        if (Directory.Exists(potentialInstallPath))
                        {
                            isInstalled = true;
                            installDir = potentialInstallPath;
                        }
                    }

                    var game = new GameMetadata
                    {
                        GameId = gameId,
                        Name = gameName,
                        IsInstalled = isInstalled,
                        InstallDirectory = installDir,
                        Source = new MetadataNameProperty("FastInstall Archive"),
                        Platforms = new HashSet<MetadataProperty> { new MetadataNameProperty("PC (Windows)") }
                    };

                    games.Add(game);
                    logger.Debug($"FastInstall: Added game '{gameName}' (Installed: {isInstalled})");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FastInstall: Error scanning archive directory.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "FastInstall_ScanError",
                    $"FastInstall: Error scanning archive: {ex.Message}",
                    NotificationType.Error));
            }

            return games;
        }

        public override LibraryMetadataProvider GetMetadataDownloader()
        {
            return null; // No metadata provider for this library
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new FastInstallController(args.Game, this);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new FastUninstallController(args.Game, this);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new FastInstallSettingsView();
        }

        /// <summary>
        /// Generates a consistent game ID from the game name
        /// </summary>
        public string GenerateGameId(string gameName)
        {
            return $"fastinstall_{gameName.ToLowerInvariant().Replace(" ", "_")}";
        }

        /// <summary>
        /// Gets the source path for a game from the archive directory
        /// </summary>
        public string GetSourcePath(Game game)
        {
            if (string.IsNullOrWhiteSpace(Settings?.SourceArchiveDirectory))
            {
                return null;
            }

            // The game name is the folder name
            return Path.Combine(Settings.SourceArchiveDirectory, game.Name);
        }

        /// <summary>
        /// Gets the destination path for a game in the fast install directory
        /// </summary>
        public string GetDestinationPath(Game game)
        {
            if (string.IsNullOrWhiteSpace(Settings?.FastInstallDirectory))
            {
                return null;
            }

            return Path.Combine(Settings.FastInstallDirectory, game.Name);
        }
    }
}

