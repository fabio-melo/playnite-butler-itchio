using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ItchioDownloader.Butler;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace ItchioDownloader.Services
{
    public class ItchInstallJob
    {
        public string GameId { get; set; }
        public ItchGame Game { get; set; }
        public ItchUpload Upload { get; set; }
        public InstallQueueResult Queue { get; set; }
        public long DownloadSizeBytes { get; set; }
        public long InstallSizeBytes { get; set; }
        public string Reason { get; set; }
        public string CaveId { get; set; }
    }

    public class ItchInstallOptions
    {
        public string GameId { get; set; }
        public ItchGame Game { get; set; }
        public List<ItchUpload> Compatible { get; set; }
        public List<ItchUpload> Incompatible { get; set; }
        public List<InstallLocationSummary> Locations { get; set; }
        public string DefaultLocationId { get; set; }
    }

    /// <summary>What the install dialog came back with.</summary>
    public class ItchInstallChoice
    {
        public ItchUpload Upload { get; set; }
        public string InstallLocationId { get; set; }
    }

    public class ItchInstallCompletedEventArgs : EventArgs
    {
        public string GameId { get; set; }
        public string CaveId { get; set; }
        public string InstallFolder { get; set; }
    }

    /// <summary>
    /// Owns the butlerd side of installing: picking an upload, queueing the task,
    /// and driving Install.Perform while translating its notifications.
    ///
    /// Queued jobs are cached per game so a paused download resumes from the same
    /// staging folder instead of starting over.
    /// </summary>
    public class ItchInstallService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;
        private readonly ConcurrentDictionary<string, ItchInstallJob> jobs =
            new ConcurrentDictionary<string, ItchInstallJob>();

        public event EventHandler<ItchInstallCompletedEventArgs> InstallCompleted;

        public ItchInstallService(ItchioDownloaderPlugin plugin)
        {
            this.plugin = plugin;
        }

        /// <summary>
        /// Uploads compatible with this machine, most recent first. Falls back to the
        /// incompatible ones when itch.io has nothing tagged for Windows — plenty of
        /// itch uploads carry no platform tags at all.
        /// </summary>
        public InstallGetUploadsResult GetUploads(long gameId)
        {
            using (var client = plugin.OpenButler())
            {
                return client.GetUploads(gameId, plugin.Settings.ProfileId);
            }
        }

        /// <summary>
        /// Everything the install dialog needs, in one butlerd round trip.
        /// </summary>
        public ItchInstallOptions GetInstallOptions(string gameId)
        {
            using (var client = plugin.OpenButler())
            {
                var numericId = long.Parse(gameId);
                var uploads = client.GetUploads(numericId, plugin.Settings.ProfileId);

                var defaultLocationId = EnsureInstallLocation(client);
                var locations = client.ListInstallLocations();

                return new ItchInstallOptions
                {
                    GameId = gameId,
                    Game = uploads?.Game ?? client.GetGame(numericId),
                    Compatible = uploads?.Uploads ?? new List<ItchUpload>(),
                    Incompatible = uploads?.IncompatibleUploads ?? new List<ItchUpload>(),
                    Locations = locations,
                    DefaultLocationId = defaultLocationId
                };
            }
        }

        /// <summary>Sizes and installer type for one upload. Slow: hits the network.</summary>
        public InstallPlanInfo PlanUpload(long uploadId)
        {
            using (var client = plugin.OpenButler())
            {
                return client.PlanUpload(uploadId);
            }
        }

        /// <summary>Registers a folder as an install location and returns its id.</summary>
        public InstallLocationSummary AddInstallLocation(string path)
        {
            using (var client = plugin.OpenButler())
            {
                Directory.CreateDirectory(path);
                return client.AddInstallLocation(path)
                    ?? client.ListInstallLocations().FirstOrDefault(l => PathsEqual(l.Path, path));
            }
        }

        public static ItchUpload PickDefaultUpload(InstallGetUploadsResult uploads)
        {
            var candidates = uploads?.Uploads;
            if (candidates == null || candidates.Count == 0)
            {
                candidates = uploads?.IncompatibleUploads;
            }

            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            // Prefer a real build over demos and preorder placeholders.
            return candidates.FirstOrDefault(u => !u.Demo && !u.Preorder) ?? candidates[0];
        }

        public ItchInstallJob GetJob(string gameId)
        {
            ItchInstallJob job;
            return jobs.TryGetValue(gameId, out job) ? job : null;
        }

        public void Discard(string gameId)
        {
            ItchInstallJob removed;
            jobs.TryRemove(gameId, out removed);
            RemovePending(gameId);
        }

        /// <summary>
        /// Queues an install (or reuses the queued one) and computes its sizes.
        ///
        /// After a Playnite restart the in-memory job is gone but UDM still has the row,
        /// so the upload, cave and reason are recovered from disk — otherwise resuming
        /// an update would silently turn into a second install.
        /// </summary>
        public ItchInstallJob Prepare(
            string gameId,
            ItchUpload upload = null,
            string caveId = null,
            string reason = "install",
            string installLocationId = null)
        {
            var existing = GetJob(gameId);
            if (existing?.Queue != null && (upload == null || existing.Upload?.Id == upload.Id))
            {
                return existing;
            }

            long recoveredUploadId = 0;
            if (upload == null && string.IsNullOrEmpty(caveId))
            {
                var recovered = GetPending(gameId);
                if (recovered != null)
                {
                    recoveredUploadId = recovered.UploadId;
                    caveId = recovered.CaveId;
                    reason = recovered.Reason ?? reason;
                }
            }

            using (var client = plugin.OpenButler())
            {
                var numericId = long.Parse(gameId);
                var uploads = client.GetUploads(numericId, plugin.Settings.ProfileId);
                var game = uploads?.Game ?? client.GetGame(numericId);
                var chosen = upload
                    ?? FindUpload(uploads, recoveredUploadId)
                    ?? PickDefaultUpload(uploads);
                if (chosen == null)
                {
                    throw new Exception("Nenhum download disponível para este item no itch.io.");
                }

                var job = new ItchInstallJob
                {
                    GameId = gameId,
                    Game = game,
                    Upload = chosen,
                    Reason = reason,
                    CaveId = caveId,
                    DownloadSizeBytes = chosen.Size
                };

                try
                {
                    var plan = client.PlanUpload(chosen.Id);
                    if (plan?.DiskUsage != null)
                    {
                        job.InstallSizeBytes = plan.DiskUsage.FinalDiskUsage;
                    }

                    if (plan?.Upload != null && plan.Upload.Size > 0)
                    {
                        job.DownloadSizeBytes = plan.Upload.Size;
                    }
                }
                catch (Exception e)
                {
                    // Planning is an optimisation; a missing size only costs us a nicer
                    // progress readout.
                    logger.Warn(e, $"Install.PlanUpload failed for game {gameId}.");
                }

                var prms = new JObject
                {
                    ["reason"] = reason,
                    ["game"] = JObject.FromObject(game),
                    ["upload"] = JObject.FromObject(chosen),
                    ["queueDownload"] = false
                };

                if (!string.IsNullOrEmpty(caveId))
                {
                    prms["caveId"] = caveId;
                }
                else
                {
                    prms["installLocationId"] = string.IsNullOrEmpty(installLocationId)
                        ? EnsureInstallLocation(client)
                        : installLocationId;
                }

                if (chosen.Build != null)
                {
                    prms["build"] = JObject.FromObject(chosen.Build);
                }

                if (plugin.Settings.ProfileId != 0)
                {
                    prms["profileId"] = plugin.Settings.ProfileId;
                }

                job.Queue = client.Queue(prms);
                if (job.Queue == null)
                {
                    throw new Exception("Install.Queue não retornou uma tarefa.");
                }

                jobs[gameId] = job;
                SavePending(gameId, new PendingInstall
                {
                    UploadId = chosen.Id,
                    CaveId = caveId,
                    Reason = reason
                });

                return job;
            }
        }

        private static ItchUpload FindUpload(InstallGetUploadsResult uploads, long uploadId)
        {
            if (uploadId == 0 || uploads == null)
            {
                return null;
            }

            return uploads.Uploads?.FirstOrDefault(u => u.Id == uploadId)
                ?? uploads.IncompatibleUploads?.FirstOrDefault(u => u.Id == uploadId);
        }

        /// <summary>
        /// Runs the queued install to completion. Returns the cave id.
        /// Cancellation is cooperative: Install.Cancel is what actually stops butler,
        /// and the staging folder survives so the next run resumes.
        /// </summary>
        public async Task<string> RunAsync(
            ItchInstallJob job,
            CancellationToken cancellation,
            Action<ProgressNotification> onProgress = null,
            Action<TaskStartedNotification> onTaskStarted = null)
        {
            using (var client = plugin.OpenButler())
            {
                EventHandler<RpcNotificationEventArgs> onNotification = (_, e) =>
                {
                    try
                    {
                        switch (e.Method)
                        {
                            case ButlerMethods.Progress:
                                onProgress?.Invoke(e.GetParams<ProgressNotification>());
                                break;
                            case ButlerMethods.TaskStarted:
                                onTaskStarted?.Invoke(e.GetParams<TaskStartedNotification>());
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "Failed to handle a butlerd install notification.");
                    }
                };

                client.NotificationReceived += onNotification;

                // butlerd only stops when told to; cancelling our own wait would leave
                // the daemon happily downloading.
                // Off the calling thread on purpose: UDM cancels from the UI thread and
                // Install.Cancel waits for butlerd to answer.
                using (cancellation.Register(() => Task.Run(() =>
                {
                    try
                    {
                        client.Cancel(job.Queue.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(ex, "Install.Cancel failed.");
                    }
                })))
                {
                    try
                    {
                        var result = await client.PerformAsync(job.Queue.Id, job.Queue.StagingFolder, CancellationToken.None)
                            .ConfigureAwait(false);

                        var caveId = result?.CaveId ?? job.Queue.CaveId;
                        job.CaveId = caveId;
                        Discard(job.GameId);

                        InstallCompleted?.Invoke(this, new ItchInstallCompletedEventArgs
                        {
                            GameId = job.GameId,
                            CaveId = caveId,
                            InstallFolder = job.Queue.InstallFolder
                        });

                        return caveId;
                    }
                    catch (RpcException e) when (IsCancellation(e))
                    {
                        throw new OperationCanceledException("Install cancelled.", e, cancellation);
                    }
                    finally
                    {
                        client.NotificationReceived -= onNotification;
                    }
                }
            }
        }

        /// <summary>butlerd reports aborted operations as 410/499.</summary>
        public static bool IsCancellation(RpcException e) => e.Code == 410 || e.Code == 499;

        public void Uninstall(string caveId)
        {
            using (var client = plugin.OpenButler())
            {
                client.Uninstall(caveId);
            }
        }

        /// <summary>
        /// Resolves (creating it if needed) the install location new games go into.
        /// </summary>
        public string EnsureInstallLocation(ButlerClient client)
        {
            var path = plugin.Settings.InstallLocationPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(plugin.DataDir, "games");
            }

            Directory.CreateDirectory(path);

            var existing = client.ListInstallLocations()
                .FirstOrDefault(l => PathsEqual(l.Path, path));
            if (existing != null)
            {
                return existing.Id;
            }

            var added = client.AddInstallLocation(path);
            if (added != null)
            {
                return added.Id;
            }

            // AddInstallLocation returns null when the path was already registered.
            existing = client.ListInstallLocations().FirstOrDefault(l => PathsEqual(l.Path, path));
            if (existing == null)
            {
                throw new Exception($"Não consegui registrar '{path}' como local de instalação.");
            }

            return existing.Id;
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Registers the itch.io app's own install folder and rebuilds cave records from
        /// the receipts butler wrote there, so games installed through the app are
        /// visible to our database too.
        /// </summary>
        public int AdoptItchAppInstalls()
        {
            var appsPath = Path.Combine(ButlerBinary.ItchUserPath, "apps");
            if (!Directory.Exists(appsPath))
            {
                return 0;
            }

            using (var client = plugin.OpenButler())
            {
                var before = client.GetCaves().Count;

                var location = client.ListInstallLocations().FirstOrDefault(l => PathsEqual(l.Path, appsPath));
                var locationId = location?.Id ?? client.AddInstallLocation(appsPath)?.Id;
                if (string.IsNullOrEmpty(locationId))
                {
                    locationId = client.ListInstallLocations()
                        .FirstOrDefault(l => PathsEqual(l.Path, appsPath))?.Id;
                }

                if (string.IsNullOrEmpty(locationId))
                {
                    logger.Warn($"Could not register '{appsPath}' as an install location.");
                    return 0;
                }

                // The scan asks the client to confirm each import it finds.
                EventHandler<RpcServerRequestEventArgs> onRequest = (_, e) =>
                {
                    if (e.Method == ButlerMethods.InstallLocationsScanYield)
                    {
                        client.Rpc.Respond(e.Id, new { });
                    }
                    else if (e.Method == ButlerMethods.InstallLocationsScanConfirmImport)
                    {
                        client.Rpc.Respond(e.Id, new { confirm = true });
                    }
                };

                client.RequestReceived += onRequest;
                try
                {
                    client.Rpc.Send(ButlerMethods.InstallLocationsScan, new { legacyMarketPath = (string)null });
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Install.Locations.Scan failed.");
                }
                finally
                {
                    client.RequestReceived -= onRequest;
                }

                var adopted = client.GetCaves().Count - before;
                logger.Info($"Adopted {adopted} itch.io app install(s) from {appsPath}.");
                return Math.Max(0, adopted);
            }
        }

        public List<Cave> GetCaves()
        {
            using (var client = plugin.OpenButler())
            {
                return client.GetCaves();
            }
        }

        // ---- Pending job persistence ----------------------------------------

        private class PendingInstall
        {
            public long UploadId { get; set; }
            public string CaveId { get; set; }
            public string Reason { get; set; }
        }

        private readonly object pendingLock = new object();
        private Dictionary<string, PendingInstall> pendingCache;

        private string PendingPath => Path.Combine(plugin.DataDir, "pending-installs.json");

        private Dictionary<string, PendingInstall> LoadPending()
        {
            if (pendingCache != null)
            {
                return pendingCache;
            }

            try
            {
                if (File.Exists(PendingPath))
                {
                    pendingCache = Newtonsoft.Json.JsonConvert
                        .DeserializeObject<Dictionary<string, PendingInstall>>(File.ReadAllText(PendingPath));
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Could not read pending-installs.json; starting fresh.");
            }

            return pendingCache = pendingCache ?? new Dictionary<string, PendingInstall>();
        }

        private PendingInstall GetPending(string gameId)
        {
            lock (pendingLock)
            {
                PendingInstall value;
                return LoadPending().TryGetValue(gameId, out value) ? value : null;
            }
        }

        private void SavePending(string gameId, PendingInstall value)
        {
            lock (pendingLock)
            {
                LoadPending()[gameId] = value;
                FlushPending();
            }
        }

        private void RemovePending(string gameId)
        {
            lock (pendingLock)
            {
                if (LoadPending().Remove(gameId))
                {
                    FlushPending();
                }
            }
        }

        private void FlushPending()
        {
            try
            {
                Directory.CreateDirectory(plugin.DataDir);
                File.WriteAllText(PendingPath, Newtonsoft.Json.JsonConvert.SerializeObject(pendingCache, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e)
            {
                logger.Warn(e, "Could not write pending-installs.json.");
            }
        }
    }
}
