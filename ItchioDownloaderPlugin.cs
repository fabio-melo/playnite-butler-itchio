using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ItchioDownloader.Butler;
using ItchioDownloader.Controllers;
using ItchioDownloader.Services;
using ItchioDownloader.Udm;
using ItchioDownloader.Views;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using UnifiedDownloadManagerApiNS;
using UnifiedDownloadManagerApiNS.Interfaces;
using UnifiedDownloadManagerApiNS.Models;

namespace ItchioDownloader
{
    /// <summary>
    /// itch.io library that downloads, installs, updates and launches games through
    /// butlerd — itch.io's own daemon — without the itch.io desktop app.
    ///
    /// Registers itself as a UnifiedDownloadManager provider, so its downloads land in
    /// the same queue as the Epic/GOG/Amazon integrations.
    /// </summary>
    public class ItchioDownloaderPlugin : LibraryPlugin, IUnifiedDownloadProvider
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderSettingsViewModel settingsViewModel;
        private readonly object daemonLock = new object();
        private ButlerDaemon daemon;

        public override Guid Id { get; } = Guid.Parse("c8a1f4e2-7b93-4d5a-9e16-2f0b8d3c6a71");

        /// <summary>
        /// Deliberately not "itch.io": Playnite's built-in plugin already uses that
        /// name, and both can be enabled at once while games are migrated across.
        /// </summary>
        public override string Name => "Butler (Itch.io)";

        public override string LibraryIcon => Path.Combine(
            Path.GetDirectoryName(typeof(ItchioDownloaderPlugin).Assembly.Location),
            "Resources", "itchio.png");

        /// <summary>Set by UDM when it resolves this plugin as a download provider.</summary>
        public IUnifiedDownloadLogic UnifiedDownloadLogic { get; set; }

        public ItchInstallService Installs { get; }

        public ItchioDownloaderSettings Settings => settingsViewModel.Settings;

        public string DataDir => GetPluginUserDataPath();

        public ItchioDownloaderPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new LibraryPluginProperties
            {
                HasSettings = true,
                CanShutdownClient = false
            };

            settingsViewModel = new ItchioDownloaderSettingsViewModel(this);
            Installs = new ItchInstallService(this);
            UnifiedDownloadLogic = new ItchDownloadLogic(this);
        }

        // ---- butlerd ---------------------------------------------------------

        private ButlerDaemon Daemon
        {
            get
            {
                lock (daemonLock)
                {
                    return daemon ?? (daemon = new ButlerDaemon(DataDir));
                }
            }
        }

        /// <summary>
        /// Opens an authenticated conversation against the shared daemon, starting it
        /// (and downloading butler, the first time) if needed.
        /// </summary>
        public ButlerClient OpenButler(Action<string> onProgress = null)
        {
            return new ButlerClient(Daemon.OpenConversation(onProgress));
        }

        // ---- UnifiedDownloadManager -----------------------------------------

        public bool IsUdmAvailable =>
            PlayniteApi.Addons.Plugins.Any(p => p.Id.Equals(UnifiedDownloadManagerSharedProperties.Id));

        /// <summary>
        /// Adds a prepared job to the UDM queue. AddTasks drives the whole download
        /// (it awaits StartDownload internally), so it is deliberately not awaited.
        /// </summary>
        public void EnqueueUdmDownload(Game game, ItchInstallJob job)
        {
            var download = new UnifiedDownload
            {
                gameID = job.GameId,
                pluginId = Id.ToString(),
                name = game.Name,
                sourceName = "itch.io",
                fullInstallPath = job.Queue?.InstallFolder,
                downloadSizeBytes = job.DownloadSizeBytes,
                installSizeBytes = job.InstallSizeBytes,
                status = UnifiedDownloadStatus.Queued
            };

            PlayniteApi.MainView.UIDispatcher.Invoke(new Action(() =>
            {
                var api = new UnifiedDownloadManagerApi();
                var running = api.AddTasks(new List<UnifiedDownload> { download }, true);
                running.ContinueWith(
                    t => logger.Error(t.Exception, "UDM AddTasks faulted."),
                    TaskContinuationOptions.OnlyOnFaulted);
            }));
        }

        // ---- Headless install contract (tanoshii / PlayniteTV) ---------------

        private HeadlessInstallService headless;

        private HeadlessInstallService Headless => headless ?? (headless = new HeadlessInstallService(this));

        /// <summary>
        /// Discovered by shape from PlayniteTV. Returns the install sheet's options as
        /// JSON so the fullscreen front-end can render them natively instead of getting
        /// a WPF window thrown over it.
        /// </summary>
        public string GetHeadlessInstallOptions(string playniteGameId) => Headless.GetOptions(playniteGameId);

        /// <summary>Counterpart of <see cref="GetHeadlessInstallOptions"/>.</summary>
        public string StartHeadlessInstall(string requestJson) => Headless.Start(requestJson);

        // ---- Install dialog --------------------------------------------------

        /// <summary>
        /// Desktop only. In fullscreen the front-end owns the UI: tanoshii collects the
        /// same choices through the headless contract above, and a WPF dialog on top of
        /// a 10-foot interface is unusable with a gamepad anyway.
        /// </summary>
        public bool CanPromptForInstallOptions =>
            PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop;

        /// <summary>
        /// Asks which upload and which folder, the way the Epic/GOG/Amazon integrations
        /// do. Returns null when the user backs out.
        /// </summary>
        public ItchInstallChoice PromptForInstallOptions(Game game)
        {
            ItchInstallOptions options = null;
            Exception failure = null;

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = $"Reading files for {game.Name}…";
                try
                {
                    options = Installs.GetInstallOptions(game.GameId);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }, new GlobalProgressOptions($"itch.io — {game.Name}", false) { IsIndeterminate = true });

            if (failure != null)
            {
                logger.Error(failure, "Could not read itch.io install options.");
                PlayniteApi.Dialogs.ShowErrorMessage(ItchInstallController.Describe(failure), "itch.io");
                return null;
            }

            var uploadCount = (options?.Compatible?.Count ?? 0) + (options?.Incompatible?.Count ?? 0);
            if (uploadCount == 0)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "itch.io lists no files to download for this item.", "itch.io");
                return null;
            }

            ItchInstallChoice choice = null;

            PlayniteApi.MainView.UIDispatcher.Invoke(new Action(() =>
            {
                var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMaximizeButton = false,
                    ShowMinimizeButton = false
                });

                window.Title = "Install via itch.io";
                window.SizeToContent = SizeToContent.WidthAndHeight;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();

                var model = new ItchInstallViewModel(this, options, window);
                window.Content = new ItchInstallView { DataContext = model };
                window.ShowDialog();

                if (model.Confirmed && model.ChosenUpload != null)
                {
                    choice = new ItchInstallChoice
                    {
                        Upload = model.ChosenUpload,
                        InstallLocationId = model.ChosenLocationId
                    };
                }
            }));

            return choice;
        }

        // ---- Library ---------------------------------------------------------

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var games = new Dictionary<string, GameMetadata>();

            if (!Settings.IsConnected)
            {
                logger.Info("No itch.io profile connected; skipping library import.");
                return games.Values;
            }

            try
            {
                using (var client = OpenButler())
                {
                    // Refresh the session so butler's cached credentials stay valid.
                    try
                    {
                        client.UseSavedLogin(Settings.ProfileId);
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, "Profile.UseSavedLogin failed; continuing with cached data.");
                    }

                    if (Settings.ImportOwnedGames)
                    {
                        // itch.io bundles mean an account can own thousands of keys, so
                        // the classification/platform cut happens on butler's side.
                        var keys = client.GetOwnedKeys(
                            Settings.ProfileId,
                            Settings.OnlyGameClassification ? "game" : null,
                            Settings.OnlyWindowsCompatible ? "windows" : null,
                            (page, total) => logger.Info($"itch.io: {total} key(s) across {page} page(s)…"),
                            args.CancelToken);

                        logger.Info($"itch.io: {keys.Count} download key(s).");

                        foreach (var key in keys)
                        {
                            var game = key.Game;
                            if (game == null || !ShouldImport(game))
                            {
                                continue;
                            }

                            games[game.Id.ToString()] = ToMetadata(game);
                        }
                    }

                    if (Settings.ImportInstalledGames && !args.CancelToken.IsCancellationRequested)
                    {
                        foreach (var cave in client.GetCaves(args.CancelToken))
                        {
                            var game = cave.Game;
                            if (game == null)
                            {
                                continue;
                            }

                            var id = game.Id.ToString();
                            GameMetadata metadata;
                            if (!games.TryGetValue(id, out metadata))
                            {
                                metadata = ToMetadata(game);
                                games[id] = metadata;
                            }

                            metadata.IsInstalled = true;
                            metadata.InstallDirectory = cave.InstallInfo?.InstallFolder;
                            metadata.Playtime = (ulong)Math.Max(0, cave.Stats?.SecondsRun ?? 0);
                            metadata.LastActivity = cave.Stats?.LastTouchedAt;
                            if (cave.Build != null)
                            {
                                metadata.Version = cave.Build.DisplayVersion;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "itch.io library import failed.");
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "itchio-import-error",
                    "itch.io: " + ItchInstallController.Describe(e),
                    NotificationType.Error));
            }

            if (args.CancelToken.IsCancellationRequested)
            {
                // A partial list would read as "the library shrank" to Playnite.
                logger.Info("itch.io: import cancelled; nothing returned.");
                return new List<GameMetadata>();
            }

            logger.Info($"itch.io: returning {games.Count} game(s) to Playnite.");
            return games.Values;
        }

        private bool ShouldImport(ItchGame game)
        {
            if (!Settings.OnlyGameClassification)
            {
                return true;
            }

            return string.IsNullOrEmpty(game.Classification) || game.Classification == "game";
        }

        private GameMetadata ToMetadata(ItchGame game)
        {
            var metadata = new GameMetadata
            {
                GameId = game.Id.ToString(),
                Name = game.Title,
                Description = game.ShortText,
                Source = new MetadataNameProperty("itch.io"),
                Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") },
                IsInstalled = false
            };

            if (!string.IsNullOrEmpty(game.BestCoverUrl))
            {
                metadata.CoverImage = new MetadataFile(game.BestCoverUrl);
            }

            if (game.User != null && !string.IsNullOrEmpty(game.User.Name))
            {
                metadata.Developers = new HashSet<MetadataProperty> { new MetadataNameProperty(game.User.Name) };
            }

            if (!string.IsNullOrEmpty(game.Url))
            {
                metadata.Links = new List<Link> { new Link("itch.io", game.Url) };
            }

            if (game.PublishedAt.HasValue)
            {
                metadata.ReleaseDate = new ReleaseDate(game.PublishedAt.Value);
            }

            return metadata;
        }

        // ---- Controllers -----------------------------------------------------

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new ItchInstallController(args.Game, this);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new ItchUninstallController(args.Game, this);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new ItchPlayController(args.Game, this);
        }

        // ---- Menu ------------------------------------------------------------

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            const string section = "@Butler (Itch.io)";

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = "Check for updates",
                Action = _ => CheckUpdatesInteractive()
            };

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = "Adopt itch.io app installs",
                Action = _ => AdoptInteractive()
            };

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = "Migrate games from the original itch.io plugin",
                Action = _ => MigrateInteractive(revert: false)
            };

            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = "Revert migration (return to the original plugin)",
                Action = _ => MigrateInteractive(revert: true)
            };
        }

        public void MigrateInteractive(bool revert)
        {
            var fromName = revert ? Name : "itch.io (built-in)";
            var toName = revert ? "itch.io (built-in)" : Name;
            var title = revert ? "Revert migration" : "Migrate games";

            var pending = revert ? Migration.CountRevertable() : Migration.CountMigratable();
            if (pending == 0)
            {
                PlayniteApi.Dialogs.ShowMessage($"No games in the {fromName} plugin.", title);
                return;
            }

            var confirm = PlayniteApi.Dialogs.ShowMessage(
                $"Move {pending} game(s) from \"{fromName}\" to \"{toName}\"?" + Environment.NewLine + Environment.NewLine +
                "Play time, tags, covers and status are preserved — only the source changes.",
                title,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            MigrationResult result = null;
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.ProgressMaxValue = pending;
                try
                {
                    Action<int, int> report = (done, total) =>
                    {
                        progress.CurrentProgressValue = done;
                        progress.Text = $"{done}/{total}";
                    };

                    result = revert ? Migration.Revert(report) : Migration.Migrate(report);
                }
                catch (Exception e)
                {
                    logger.Error(e, "itch.io migration failed.");
                    PlayniteApi.Dialogs.ShowErrorMessage(ItchInstallController.Describe(e), title);
                }
            }, new GlobalProgressOptions(title, false) { IsIndeterminate = false });

            if (result == null)
            {
                return;
            }

            var message = $"{result.Moved} game(s) moved.";
            if (result.Skipped > 0)
            {
                message += Environment.NewLine +
                           $"{result.Skipped} skipped because they already exist in \"{toName}\".";
            }

            if (!revert && result.Moved > 0)
            {
                message += Environment.NewLine + Environment.NewLine +
                           "Installed games only stay marked as installed after " +
                           "\"Adopt itch.io app installs\".";
            }

            PlayniteApi.Dialogs.ShowMessage(message, title);
        }

        private void CheckUpdatesInteractive()
        {
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = "Checking itch.io for updates…";
                try
                {
                    var updates = Updates.Check();
                    var message = updates.Count == 0
                        ? "No pending updates."
                        : string.Join(Environment.NewLine, updates.Select(u => "• " + u.Game?.Title));

                    PlayniteApi.Dialogs.ShowMessage(message, "itch.io");
                }
                catch (Exception e)
                {
                    logger.Error(e, "CheckUpdate failed.");
                    PlayniteApi.Dialogs.ShowErrorMessage(ItchInstallController.Describe(e), "itch.io");
                }
            }, new GlobalProgressOptions("itch.io", false) { IsIndeterminate = true });
        }

        public void AdoptInteractive()
        {
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = "Looking for games installed by the itch.io app…";
                try
                {
                    var adopted = Installs.AdoptItchAppInstalls();
                    PlayniteApi.Dialogs.ShowMessage(
                        adopted == 0
                            ? "Nothing new found."
                            : $"{adopted} install(s) adopted. Refresh the library to see them.",
                        "itch.io");
                }
                catch (Exception e)
                {
                    logger.Error(e, "Adoption scan failed.");
                    PlayniteApi.Dialogs.ShowErrorMessage(ItchInstallController.Describe(e), "itch.io");
                }
            }, new GlobalProgressOptions("itch.io", false) { IsIndeterminate = true });
        }

        // ---- Updates ---------------------------------------------------------

        public ItchUpdateService Updates => updates ?? (updates = new ItchUpdateService(this));
        private ItchUpdateService updates;

        // ---- Migration -------------------------------------------------------

        public ItchMigrationService Migration => migration ?? (migration = new ItchMigrationService(this));
        private ItchMigrationService migration;

        // ---- Settings --------------------------------------------------------

        public override ISettings GetSettings(bool firstRunSettings) => settingsViewModel;

        public override UserControl GetSettingsView(bool firstRunSettings) =>
            new ItchioDownloaderSettingsView();

        public void OnSettingsChanged()
        {
            if (Settings.AdoptItchAppInstalls)
            {
                Task.Run(() =>
                {
                    try
                    {
                        Installs.AdoptItchAppInstalls();
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, "Background adoption scan failed.");
                    }
                });
            }
        }

        public override void Dispose()
        {
            lock (daemonLock)
            {
                daemon?.Dispose();
                daemon = null;
            }

            base.Dispose();
        }
    }
}
