using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ItchioDownloader.Butler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace ItchioDownloader.Services
{
    /// <summary>
    /// A JSON contract for front-ends that render their own install UI — today that
    /// means tanoshii, which drives the fullscreen experience over its WebSocket
    /// bridge and must never see a WPF window.
    ///
    /// PlayniteTV finds these by shape (a method named GetHeadlessInstallOptions
    /// taking a string), never by plugin name, so nothing here creates a hard
    /// dependency in either direction. JSON is the interop layer precisely because
    /// the two plugins share no types.
    /// </summary>
    public class HeadlessInstallService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;

        public HeadlessInstallService(ItchioDownloaderPlugin plugin)
        {
            this.plugin = plugin;
        }

        /// <param name="playniteGameId">Playnite's Game.Id, as a Guid string.</param>
        public string GetOptions(string playniteGameId)
        {
            try
            {
                Guid id;
                if (!Guid.TryParse(playniteGameId, out id))
                {
                    return Unsupported("bad_game_id");
                }

                var game = plugin.PlayniteApi.Database.Games.Get(id);
                if (game == null || game.PluginId != plugin.Id)
                {
                    return Unsupported("game_not_found");
                }

                if (game.IsInstalled)
                {
                    return Unsupported("already_installed");
                }

                var options = plugin.Installs.GetInstallOptions(game.GameId);
                var variants = BuildVariants(options);
                if (variants.Count == 0)
                {
                    return Unsupported("no_uploads");
                }

                var payload = new JObject
                {
                    ["supported"] = true,
                    ["id"] = game.Id.ToString(),
                    ["name"] = game.Name,
                    ["source"] = "itch.io",
                    // Install.PlanUpload is a network round trip per upload, far too slow
                    // for a sheet that has to open instantly. The picked upload's own
                    // size is the honest number we have up front.
                    ["downloadBytes"] = variants
                        .Select(v => (long?)v["sizeBytes"])
                        .FirstOrDefault(s => s.HasValue && s.Value > 0) ?? 0,
                    ["installBytes"] = null,
                    ["paths"] = BuildPaths(options),
                    ["extras"] = new JArray(),
                    ["variants"] = new JArray(variants)
                };

                return payload.ToString(Formatting.None);
            }
            catch (Exception e)
            {
                logger.Error(e, "itch.io headless install options failed.");
                return Unsupported("options_failed");
            }
        }

        private static List<JObject> BuildVariants(ItchInstallOptions options)
        {
            var variants = new List<JObject>();

            Action<ItchUpload, bool> add = (upload, compatible) =>
            {
                variants.Add(new JObject
                {
                    ["id"] = upload.Id.ToString(),
                    ["label"] = upload.Label,
                    ["detail"] = Describe(upload, compatible),
                    ["sizeBytes"] = upload.Size,
                    ["compatible"] = compatible,
                    ["isDefault"] = false
                });
            };

            foreach (var upload in options.Compatible ?? new List<ItchUpload>())
            {
                add(upload, true);
            }

            // Untagged uploads are common on itch.io; hiding them outright would make
            // some games impossible to install from the couch.
            foreach (var upload in options.Incompatible ?? new List<ItchUpload>())
            {
                add(upload, false);
            }

            var preferred = variants.FirstOrDefault(v => (bool)v["compatible"]) ?? variants.FirstOrDefault();
            if (preferred != null)
            {
                preferred["isDefault"] = true;
            }

            return variants;
        }

        private static string Describe(ItchUpload upload, bool compatible)
        {
            var parts = new List<string>();

            var platforms = new List<string>();
            if (!string.IsNullOrEmpty(upload.Platforms?.Windows)) platforms.Add("Windows");
            if (!string.IsNullOrEmpty(upload.Platforms?.Linux)) platforms.Add("Linux");
            if (!string.IsNullOrEmpty(upload.Platforms?.Osx)) platforms.Add("macOS");
            if (platforms.Count > 0) parts.Add(string.Join("/", platforms));

            if (!string.IsNullOrEmpty(upload.ChannelName)) parts.Add(upload.ChannelName);
            if (upload.Build != null) parts.Add("v" + upload.Build.DisplayVersion);
            if (upload.Demo) parts.Add("demo");
            if (upload.Preorder) parts.Add("preorder");
            if (!compatible) parts.Add("not tagged for this system");

            return string.Join(" · ", parts);
        }

        private static JArray BuildPaths(ItchInstallOptions options)
        {
            var paths = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<string, bool> add = (path, isDefault) =>
            {
                if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                {
                    return;
                }

                paths.Add(new JObject
                {
                    ["path"] = path,
                    ["label"] = DriveLabel(path),
                    ["freeBytes"] = FreeSpace(path),
                    ["isDefault"] = isDefault
                });
            };

            foreach (var location in options.Locations ?? new List<InstallLocationSummary>())
            {
                add(location.Path, location.Id == options.DefaultLocationId);
            }

            // No folder browser on a TV: one sane target per fixed disk keeps other
            // drives reachable from the couch.
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    add(Path.Combine(drive.RootDirectory.FullName, "Games", "itch"), false);
                }
            }
            catch (Exception e)
            {
                logger.Debug("itch.io: drive enumeration failed: " + e.Message);
            }

            return paths;
        }

        public string Start(string requestJson)
        {
            try
            {
                var request = JObject.Parse(requestJson ?? "{}");

                Guid id;
                if (!Guid.TryParse((string)request["id"], out id))
                {
                    return Failed("bad_game_id");
                }

                var game = plugin.PlayniteApi.Database.Games.Get(id);
                if (game == null || game.PluginId != plugin.Id)
                {
                    return Failed("game_not_found");
                }

                var variantId = (string)request["variant"];
                var path = (string)request["path"];

                var uploads = plugin.Installs.GetUploads(long.Parse(game.GameId));
                var upload = FindUpload(uploads, variantId) ?? ItchInstallService.PickDefaultUpload(uploads);
                if (upload == null)
                {
                    return Failed("no_uploads");
                }

                string locationId = null;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var location = plugin.Installs.AddInstallLocation(path);
                    if (location == null)
                    {
                        return Failed("bad_install_path");
                    }

                    locationId = location.Id;
                }

                var job = plugin.Installs.Prepare(game.GameId, upload, null, "install", locationId);

                if (plugin.IsUdmAvailable)
                {
                    plugin.EnqueueUdmDownload(game, job);
                }
                else
                {
                    // Without UDM there is no queue to hand off to, so the download runs
                    // here and the front-end watches the library for the result.
                    plugin.Installs.RunAsync(job, CancellationToken.None)
                        .ContinueWith(t => logger.Error(t.Exception, "itch.io headless install failed."),
                            TaskContinuationOptions.OnlyOnFaulted);
                }

                return new JObject
                {
                    ["ok"] = true,
                    ["path"] = job.Queue?.InstallFolder ?? path
                }.ToString(Formatting.None);
            }
            catch (Exception e)
            {
                logger.Error(e, "itch.io headless install start failed.");
                return Failed("install_failed");
            }
        }

        private static ItchUpload FindUpload(InstallGetUploadsResult uploads, string variantId)
        {
            long id;
            if (uploads == null || !long.TryParse(variantId, out id))
            {
                return null;
            }

            return uploads.Uploads?.FirstOrDefault(u => u.Id == id)
                ?? uploads.IncompatibleUploads?.FirstOrDefault(u => u.Id == id);
        }

        private static string Unsupported(string reason) =>
            new JObject { ["supported"] = false, ["reason"] = reason }.ToString(Formatting.None);

        private static string Failed(string reason) =>
            new JObject { ["ok"] = false, ["reason"] = reason }.ToString(Formatting.None);

        private static string DriveLabel(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root))
                {
                    return path;
                }

                var drive = new DriveInfo(root);
                var letter = root.TrimEnd('\\', '/');
                var name = drive.IsReady ? drive.VolumeLabel : null;
                return string.IsNullOrWhiteSpace(name) ? letter : $"{name} ({letter})";
            }
            catch
            {
                return path;
            }
        }

        private static long FreeSpace(string path)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                return 0;
            }
        }
    }
}
