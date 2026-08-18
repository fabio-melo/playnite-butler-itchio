using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ItchioDownloader.Butler
{
    /// <summary>
    /// Typed wrappers over a single butlerd conversation. One instance owns one
    /// connection; long-running calls (Install.Perform, Launch) get their own so their
    /// notifications don't cross-talk with anything else.
    /// </summary>
    public class ButlerClient : IDisposable
    {
        private readonly ButlerConversation conversation;

        public ButlerClient(ButlerConversation conversation)
        {
            this.conversation = conversation;
        }

        public JsonRpcClient Rpc => conversation.Client;

        public event EventHandler<RpcNotificationEventArgs> NotificationReceived
        {
            add { conversation.Client.NotificationReceived += value; }
            remove { conversation.Client.NotificationReceived -= value; }
        }

        public event EventHandler<RpcServerRequestEventArgs> RequestReceived
        {
            add { conversation.Client.RequestReceived += value; }
            remove { conversation.Client.RequestReceived -= value; }
        }

        // ---- Profile ---------------------------------------------------------

        public Profile LoginWithApiKey(string apiKey) =>
            Rpc.Send<ProfileResult>(ButlerMethods.ProfileLoginWithApiKey, new { apiKey })?.Profile;

        public List<Profile> ListProfiles() =>
            Rpc.Send<ProfileListResult>(ButlerMethods.ProfileList)?.Profiles ?? new List<Profile>();

        public Profile UseSavedLogin(long profileId) =>
            Rpc.Send<ProfileResult>(ButlerMethods.ProfileUseSavedLogin, new { profileId })?.Profile;

        public void Forget(long profileId) =>
            Rpc.Send(ButlerMethods.ProfileForget, new { profileId });

        // ---- Library ---------------------------------------------------------

        /// <summary>
        /// Every download key the profile owns, paged.
        ///
        /// Filtering happens server-side on purpose: bundle-heavy accounts own
        /// thousands of keys, and most of them are assets, tools and soundtracks that
        /// have no business in a game library.
        ///
        /// Fetches from cache first and only re-fetches over the network when butler
        /// says the data is stale — the documented pattern, and the difference between
        /// a two-second and a two-minute library update.
        /// </summary>
        public List<DownloadKey> GetOwnedKeys(
            long profileId,
            string classification = null,
            string platform = null,
            Action<int, int> onPage = null,
            CancellationToken token = default(CancellationToken))
        {
            bool stale;
            var items = FetchOwnedKeyPages(profileId, false, classification, platform, onPage, token, out stale);
            if (stale && !token.IsCancellationRequested)
            {
                items = FetchOwnedKeyPages(profileId, true, classification, platform, onPage, token, out stale);
            }

            return items;
        }

        private List<DownloadKey> FetchOwnedKeyPages(
            long profileId,
            bool fresh,
            string classification,
            string platform,
            Action<int, int> onPage,
            CancellationToken token,
            out bool stale)
        {
            var all = new List<DownloadKey>();
            string cursor = null;
            var page = 0;
            stale = false;

            do
            {
                // Paging is the only cancellation point we get: each Fetch call itself
                // is a blocking round-trip to butlerd.
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var prms = new JObject { ["profileId"] = profileId };
                if (fresh)
                {
                    prms["fresh"] = true;
                }

                if (!string.IsNullOrEmpty(cursor))
                {
                    prms["cursor"] = cursor;
                }

                var filters = new JObject();
                if (!string.IsNullOrEmpty(classification))
                {
                    filters["classification"] = classification;
                }

                if (!string.IsNullOrEmpty(platform))
                {
                    filters["platform"] = platform;
                }

                if (filters.HasValues)
                {
                    prms["filters"] = filters;
                }

                var result = Rpc.Send<PagedResult<DownloadKey>>(ButlerMethods.FetchProfileOwnedKeys, prms);
                if (result == null)
                {
                    break;
                }

                if (page == 0)
                {
                    stale = result.Stale;
                }

                if (result.Items != null)
                {
                    all.AddRange(result.Items);
                }

                page++;
                onPage?.Invoke(page, all.Count);
                cursor = result.NextCursor;
            }
            while (!string.IsNullOrEmpty(cursor));

            return all;
        }

        public List<Cave> GetCaves(CancellationToken token = default(CancellationToken))
        {
            return FetchAllPages<Cave>(ButlerMethods.FetchCaves, cursor =>
            {
                var prms = new JObject();
                if (!string.IsNullOrEmpty(cursor))
                {
                    prms["cursor"] = cursor;
                }

                return prms;
            }, token);
        }

        public Cave GetCave(string caveId) =>
            Rpc.Send<JObject>(ButlerMethods.FetchCave, new { caveId })?["cave"]?.ToObject<Cave>();

        public ItchGame GetGame(long gameId, bool fresh = false)
        {
            var result = Rpc.Send<FetchGameResult>(ButlerMethods.FetchGame, new { gameId });
            if (result != null && result.Stale && !fresh)
            {
                result = Rpc.Send<FetchGameResult>(ButlerMethods.FetchGame, new { gameId, fresh = true });
            }

            return result?.Game;
        }

        private List<T> FetchAllPages<T>(
            string method,
            Func<string, JObject> buildParams,
            CancellationToken token = default(CancellationToken))
        {
            var all = new List<T>();
            string cursor = null;

            do
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var page = Rpc.Send<PagedResult<T>>(method, buildParams(cursor));
                if (page == null)
                {
                    break;
                }

                if (page.Items != null)
                {
                    all.AddRange(page.Items);
                }

                cursor = page.NextCursor;
            }
            while (!string.IsNullOrEmpty(cursor));

            return all;
        }

        // ---- Install locations ----------------------------------------------

        public List<InstallLocationSummary> ListInstallLocations() =>
            Rpc.Send<InstallLocationsListResult>(ButlerMethods.InstallLocationsList)?.InstallLocations
            ?? new List<InstallLocationSummary>();

        /// <summary>
        /// Adds a location. Returns null when a location with that path already exists,
        /// which butlerd treats as success.
        /// </summary>
        public InstallLocationSummary AddInstallLocation(string path, string id = null)
        {
            var prms = new JObject { ["path"] = path };
            if (!string.IsNullOrEmpty(id))
            {
                prms["id"] = id;
            }

            return Rpc.Send<InstallLocationsAddResult>(ButlerMethods.InstallLocationsAdd, prms)?.InstallLocation;
        }

        // ---- Install ---------------------------------------------------------

        public InstallGetUploadsResult GetUploads(long gameId, long profileId = 0)
        {
            var prms = new JObject { ["gameId"] = gameId };
            if (profileId != 0)
            {
                prms["profileId"] = profileId;
            }

            return Rpc.Send<InstallGetUploadsResult>(ButlerMethods.InstallGetUploads, prms);
        }

        public InstallPlanInfo PlanUpload(long uploadId, string id = null)
        {
            var prms = new JObject { ["uploadId"] = uploadId };
            if (!string.IsNullOrEmpty(id))
            {
                prms["id"] = id;
            }

            return Rpc.Send<InstallPlanUploadResult>(ButlerMethods.InstallPlanUpload, prms)?.Info;
        }

        public InstallQueueResult Queue(JObject prms) =>
            Rpc.Send<InstallQueueResult>(ButlerMethods.InstallQueue, prms);

        public Task<InstallPerformResult> PerformAsync(string id, string stagingFolder, CancellationToken token) =>
            Rpc.SendAsync<InstallPerformResult>(ButlerMethods.InstallPerform, new { id, stagingFolder }, token);

        public bool Cancel(string id) =>
            Rpc.Send<InstallCancelResult>(ButlerMethods.InstallCancel, new { id })?.DidCancel ?? false;

        public void Uninstall(string caveId, bool hard = false) =>
            Rpc.Send(ButlerMethods.UninstallPerform, new { caveId, hard });

        // ---- Updates ---------------------------------------------------------

        public CheckUpdateResult CheckUpdate(List<string> caveIds = null)
        {
            var prms = new JObject();
            if (caveIds != null && caveIds.Count > 0)
            {
                prms["caveIds"] = new JArray(caveIds);
            }

            return Rpc.Send<CheckUpdateResult>(ButlerMethods.CheckUpdate, prms);
        }

        // ---- Launch ----------------------------------------------------------

        public Task LaunchAsync(string caveId, string prereqsDir, CancellationToken token = default(CancellationToken)) =>
            Rpc.SendAsync(ButlerMethods.Launch, new { caveId, prereqsDir }, token);

        public void Dispose() => conversation.Dispose();
    }
}
