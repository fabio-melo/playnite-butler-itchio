using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ItchioDownloader.Butler;
using ItchioDownloader.Services;
using Playnite.SDK;
using UnifiedDownloadManagerApiNS.Interfaces;
using UnifiedDownloadManagerApiNS.Models;

namespace ItchioDownloader.Udm
{
    /// <summary>
    /// Bridges butlerd installs into the UnifiedDownloadManager queue.
    ///
    /// The contract, read off UDM's TaskManager: DoNextJobInQueue awaits
    /// StartDownload, so this method must block for the whole download and is the
    /// only thing that writes the task's progress and final status. Pausing shows up
    /// as gracefulCts firing with status already set to Paused.
    /// </summary>
    public class ItchDownloadLogic : IUnifiedDownloadLogic
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;

        public ItchDownloadLogic(ItchioDownloaderPlugin plugin)
        {
            this.plugin = plugin;
        }

        private ItchInstallService Installs => plugin.Installs;

        public async Task StartDownload(UnifiedDownload downloadTask)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                downloadTask.gracefulCts?.Token ?? CancellationToken.None,
                downloadTask.forcefulCts?.Token ?? CancellationToken.None);

            downloadTask.status = UnifiedDownloadStatus.Running;
            Set(downloadTask, t => t.activity = "Preparando…");

            try
            {
                var job = await Task.Run(() => Installs.Prepare(downloadTask.gameID), linked.Token)
                    .ConfigureAwait(false);

                Set(downloadTask, t =>
                {
                    if (job.DownloadSizeBytes > 0)
                    {
                        t.downloadSizeBytes = job.DownloadSizeBytes;
                    }

                    if (job.InstallSizeBytes > 0)
                    {
                        t.installSizeBytes = job.InstallSizeBytes;
                    }

                    if (!string.IsNullOrEmpty(job.Queue?.InstallFolder))
                    {
                        t.fullInstallPath = job.Queue.InstallFolder;
                    }

                    t.activity = string.Empty;
                });

                var elapsed = System.Diagnostics.Stopwatch.StartNew();

                await Installs.RunAsync(
                    job,
                    linked.Token,
                    progress => Set(downloadTask, t =>
                    {
                        t.progress = Math.Max(0, Math.Min(100, progress.Progress * 100));
                        if (t.downloadSizeBytes > 0)
                        {
                            t.downloadedBytes = t.downloadSizeBytes * progress.Progress;
                        }

                        t.downloadSpeedBytes = progress.Bps;
                        t.eta = progress.Eta > 0 ? TimeSpan.FromSeconds(progress.Eta) : TimeSpan.Zero;
                        t.elapsed = elapsed.Elapsed;
                    }),
                    taskStarted => Set(downloadTask, t =>
                    {
                        t.activity = DescribeTask(taskStarted.Type);
                        if (taskStarted.TotalSize > 0 && taskStarted.Type == "download")
                        {
                            t.downloadSizeBytes = taskStarted.TotalSize;
                        }
                    })).ConfigureAwait(false);

                elapsed.Stop();

                Set(downloadTask, t =>
                {
                    t.activity = string.Empty;
                    t.progress = 100.0;
                    t.downloadedBytes = t.downloadSizeBytes;
                    t.downloadSpeedBytes = 0;
                    t.eta = TimeSpan.Zero;
                    t.status = UnifiedDownloadStatus.Completed;
                    t.completedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                });
            }
            catch (OperationCanceledException)
            {
                // Pause and cancel both land here; UDM already set the right status.
                Set(downloadTask, t =>
                {
                    t.activity = string.Empty;
                    t.downloadSpeedBytes = 0;
                });
            }
            catch (Exception e)
            {
                var message = (e as RpcException)?.UserMessage ?? e.Message;
                logger.Error(e, $"itch.io download failed for {downloadTask.name}.");
                Set(downloadTask, t =>
                {
                    t.activity = message;
                    t.downloadSpeedBytes = 0;
                    t.status = UnifiedDownloadStatus.Error;
                });
            }
            finally
            {
                linked.Dispose();
            }
        }

        public Task OnCancelDownload(UnifiedDownload downloadTask)
        {
            // The staging folder is only worth keeping for a paused download; a
            // cancelled one should not eat disk.
            var job = Installs.GetJob(downloadTask.gameID);
            var staging = job?.Queue?.StagingFolder;
            Installs.Discard(downloadTask.gameID);

            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, true);
                }
                catch (Exception e)
                {
                    logger.Warn(e, $"Could not remove staging folder {staging}.");
                }
            }

            return Task.CompletedTask;
        }

        public Task OnRemoveDownloadEntry(UnifiedDownload downloadTask)
        {
            Installs.Discard(downloadTask.gameID);
            return Task.CompletedTask;
        }

        public void OpenDownloadPropertiesWindow(UnifiedDownload selectedEntry)
        {
            var job = Installs.GetJob(selectedEntry.gameID);
            var lines = new[]
            {
                selectedEntry.name,
                string.Empty,
                "Origem: itch.io",
                "Arquivo: " + (job?.Upload?.Label ?? "—"),
                "Canal: " + (string.IsNullOrEmpty(job?.Upload?.ChannelName) ? "—" : job.Upload.ChannelName),
                "Instalar em: " + (selectedEntry.fullInstallPath ?? job?.Queue?.InstallFolder ?? "—")
            };

            plugin.PlayniteApi.Dialogs.ShowMessage(string.Join(Environment.NewLine, lines), "itch.io");
        }

        private static string DescribeTask(string type)
        {
            switch (type)
            {
                case "download": return "Baixando";
                case "install": return "Instalando";
                case "update": return "Aplicando patch";
                case "heal": return "Verificando arquivos";
                case "uninstall": return "Desinstalando";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// UnifiedDownload is bound to WPF, so writes go through the dispatcher.
        /// </summary>
        private static void Set(UnifiedDownload task, Action<UnifiedDownload> mutate)
        {
            var app = Application.Current;
            if (app?.Dispatcher == null || app.Dispatcher.CheckAccess())
            {
                mutate(task);
                return;
            }

            app.Dispatcher.BeginInvoke((Action)(() => mutate(task)));
        }
    }
}
