using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ItchioDownloader.Butler;
using ItchioDownloader.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace ItchioDownloader.Controllers
{
    /// <summary>
    /// Hands the install to the UnifiedDownloadManager queue when it is available, and
    /// runs it inline otherwise. Either way butlerd does the work — the itch.io app is
    /// never involved.
    /// </summary>
    public class ItchInstallController : InstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;
        private CancellationTokenSource cts;
        private EventHandler<ItchInstallCompletedEventArgs> completedHandler;

        public ItchInstallController(Game game, ItchioDownloaderPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Instalar pelo itch.io";
        }

        public override void Dispose()
        {
            if (completedHandler != null)
            {
                plugin.Installs.InstallCompleted -= completedHandler;
                completedHandler = null;
            }

            cts?.Cancel();
            cts?.Dispose();
            cts = null;
        }

        public override void Install(InstallActionArgs args)
        {
            var gameId = Game.GameId;

            // itch.io pages routinely carry several uploads — Windows/Linux builds,
            // demos, soundtracks — so picking one silently is the wrong default.
            //
            // Fullscreen is the exception: tanoshii asks the same questions through the
            // headless contract and calls StartHeadlessInstall, so reaching this
            // controller there means nobody is going to answer a dialog. Fall back to
            // the sensible defaults rather than blocking on a window no one can drive.
            ItchInstallChoice choice;
            if (plugin.CanPromptForInstallOptions)
            {
                choice = plugin.PromptForInstallOptions(Game);
                if (choice == null)
                {
                    Cancelled();
                    return;
                }
            }
            else
            {
                choice = new ItchInstallChoice();
            }

            completedHandler = (_, e) =>
            {
                if (e.GameId != gameId)
                {
                    return;
                }

                InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData
                {
                    InstallDirectory = e.InstallFolder
                }));

                Dispose();
            };

            plugin.Installs.InstallCompleted += completedHandler;

            if (plugin.IsUdmAvailable)
            {
                QueueThroughUdm(gameId, choice);
            }
            else
            {
                RunInline(gameId, choice);
            }
        }

        private void QueueThroughUdm(string gameId, ItchInstallChoice choice)
        {
            plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = $"Preparando {Game.Name}…";
                try
                {
                    var job = plugin.Installs.Prepare(gameId, choice.Upload, null, "install", choice.InstallLocationId);
                    plugin.EnqueueUdmDownload(Game, job);
                }
                catch (Exception e)
                {
                    logger.Error(e, "Failed to queue an itch.io install.");
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(Describe(e), "itch.io");
                    Cancelled();
                }
            }, new GlobalProgressOptions($"itch.io — {Game.Name}", false) { IsIndeterminate = true });
        }

        private void RunInline(string gameId, ItchInstallChoice choice)
        {
            cts = new CancellationTokenSource();
            var token = cts.Token;

            Task.Run(() =>
            {
                plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
                {
                    progress.Text = $"Preparando {Game.Name}…";
                    try
                    {
                        var job = plugin.Installs.Prepare(gameId, choice.Upload, null, "install", choice.InstallLocationId);
                        progress.ProgressMaxValue = 100;

                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, progress.CancelToken))
                        {
                            plugin.Installs.RunAsync(
                                job,
                                linked.Token,
                                p =>
                                {
                                    progress.CurrentProgressValue = p.Progress * 100;
                                    progress.Text = $"{Game.Name} — {Math.Round(p.Progress * 100)}%";
                                }).GetAwaiter().GetResult();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Cancelled();
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "itch.io install failed.");
                        plugin.PlayniteApi.Dialogs.ShowErrorMessage(Describe(e), "itch.io");
                        InvokeOnInstalled(null);
                    }
                }, new GlobalProgressOptions($"itch.io — {Game.Name}", true) { IsIndeterminate = false });
            });
        }

        /// <summary>
        /// Releases Playnite's "installing" state. It has to be
        /// InvokeOnInstallationCancelled — InvokeOnInstalled(null) throws a
        /// NullReferenceException inside Playnite, which is what a cancelled dialog
        /// used to produce.
        /// </summary>
        private void Cancelled()
        {
            Dispose();
            InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
        }

        internal static string Describe(Exception e) => (e as RpcException)?.UserMessage ?? e.Message;
    }

    public class ItchUninstallController : UninstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;

        public ItchUninstallController(Game game, ItchioDownloaderPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Desinstalar pelo itch.io";
        }

        public override void Dispose()
        {
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.Text = $"Desinstalando {Game.Name}…";
                try
                {
                    var caves = plugin.Installs.GetCaves();
                    var matches = caves.Where(c => c.Game?.Id.ToString() == Game.GameId).ToList();
                    if (matches.Count == 0)
                    {
                        // Nothing on disk as far as butler knows; just clear the flag.
                        InvokeOnUninstalled(new GameUninstalledEventArgs());
                        return;
                    }

                    foreach (var cave in matches)
                    {
                        plugin.Installs.Uninstall(cave.Id);
                    }

                    InvokeOnUninstalled(new GameUninstalledEventArgs());
                }
                catch (Exception e)
                {
                    logger.Error(e, "itch.io uninstall failed.");
                    plugin.PlayniteApi.Dialogs.ShowErrorMessage(ItchInstallController.Describe(e), "itch.io");
                }
            }, new GlobalProgressOptions($"itch.io — {Game.Name}", false) { IsIndeterminate = true });
        }
    }

    /// <summary>
    /// Launches through butlerd, which installs prerequisites and answers the
    /// interactive prompts (manifest actions, licenses, HTML5 games) itself.
    /// </summary>
    public class ItchPlayController : PlayController
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;
        private ButlerClient client;
        private Stopwatch stopWatch;

        public ItchPlayController(Game game, ItchioDownloaderPlugin plugin) : base(game)
        {
            this.plugin = plugin;
            Name = "Jogar pelo itch.io";
        }

        public override void Dispose()
        {
            if (client != null)
            {
                client.NotificationReceived -= OnNotification;
                client.RequestReceived -= OnRequest;
                client.Dispose();
                client = null;
            }
        }

        public override void Play(PlayActionArgs args)
        {
            Dispose();

            try
            {
                client = plugin.OpenButler();
                var cave = client.GetCaves().FirstOrDefault(c => c.Game?.Id.ToString() == Game.GameId);
                if (cave == null)
                {
                    throw new Exception("Instalação não encontrada para este jogo.");
                }

                client.NotificationReceived += OnNotification;
                client.RequestReceived += OnRequest;

                Directory.CreateDirectory(ButlerBinary.PrereqsPath);

                // Launch blocks for the game's whole lifetime; LaunchExited is what
                // tells Playnite the session is over.
                client.LaunchAsync(cave.Id, ButlerBinary.PrereqsPath)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            logger.Error(t.Exception, "itch.io launch failed.");
                            InvokeOnStopped(new GameStoppedEventArgs(0));
                        }
                    }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception e)
            {
                logger.Error(e, "itch.io launch failed.");
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(ItchInstallController.Describe(e), "itch.io");
                InvokeOnStopped(new GameStoppedEventArgs(0));
            }
        }

        private void OnNotification(object sender, RpcNotificationEventArgs e)
        {
            if (e.Method == ButlerMethods.LaunchRunning)
            {
                stopWatch = Stopwatch.StartNew();
                InvokeOnStarted(new GameStartedEventArgs());
            }
            else if (e.Method == ButlerMethods.LaunchExited)
            {
                stopWatch?.Stop();
                InvokeOnStopped(new GameStoppedEventArgs(
                    Convert.ToUInt64(stopWatch?.Elapsed.TotalSeconds ?? 0)));
            }
        }

        private void OnRequest(object sender, RpcServerRequestEventArgs e)
        {
            try
            {
                switch (e.Method)
                {
                    case ButlerMethods.PickManifestAction:
                        client.Rpc.Respond(e.Id, new { index = PickAction(e.GetParams<PickManifestActionParams>()) });
                        break;

                    case ButlerMethods.AcceptLicense:
                        var license = e.GetParams<AcceptLicenseParams>();
                        var accepted = plugin.PlayniteApi.Dialogs.ShowMessage(
                            license?.Text ?? string.Empty,
                            "itch.io",
                            System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes;
                        client.Rpc.Respond(e.Id, new { accepted });
                        break;

                    case ButlerMethods.ShellLaunch:
                        Process.Start(e.GetParams<ShellLaunchParams>().ItemPath);
                        client.Rpc.Respond(e.Id, new { });
                        break;

                    case ButlerMethods.UrlLaunch:
                        Process.Start(e.GetParams<UrlLaunchParams>().Url);
                        client.Rpc.Respond(e.Id, new { });
                        break;

                    case ButlerMethods.HtmlLaunch:
                        var html = e.GetParams<HtmlLaunchParams>();
                        Process.Start(Path.Combine(html.RootFolder, html.IndexPath));
                        client.Rpc.Respond(e.Id, new { });
                        break;

                    case ButlerMethods.PrereqsFailed:
                        // Missing redistributables usually still leave a playable game.
                        client.Rpc.Respond(e.Id, new { continueWithoutPrereqs = true });
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to answer butlerd request {e.Method}.");
                client.Rpc.RespondError(e.Id, 0, ex.Message);
            }
        }

        private long PickAction(PickManifestActionParams prms)
        {
            var actions = prms?.Actions;
            if (actions == null || actions.Count == 0)
            {
                return -1;
            }

            if (actions.Count == 1)
            {
                return 0;
            }

            var options = actions.Select(a => new MessageBoxOption(a.Label)).ToList();
            var chosen = plugin.PlayniteApi.Dialogs.ShowMessage(
                "O que você quer abrir?",
                Game.Name,
                System.Windows.MessageBoxImage.Question,
                options);

            var index = options.FindIndex(o => o == chosen);
            return index < 0 ? -1 : index;
        }
    }
}
