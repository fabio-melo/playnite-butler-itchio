using System;
using System.Collections.Generic;
using System.Linq;
using ItchioDownloader.Butler;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ItchioDownloader.Services
{
    /// <summary>
    /// Manual update checking. CheckUpdate walks every installed cave and asks itch.io
    /// whether a newer upload or build exists, so it is deliberately not run on a timer.
    /// </summary>
    public class ItchUpdateService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ItchioDownloaderPlugin plugin;

        public ItchUpdateService(ItchioDownloaderPlugin plugin)
        {
            this.plugin = plugin;
        }

        /// <summary>Last successful check, so callers can show something without re-checking.</summary>
        public List<GameUpdate> LastResults { get; private set; } = new List<GameUpdate>();

        public DateTime? LastCheckedAt { get; private set; }

        public List<GameUpdate> Check(List<string> caveIds = null)
        {
            using (var client = plugin.OpenButler())
            {
                var result = client.CheckUpdate(caveIds);
                if (result?.Warnings != null)
                {
                    foreach (var warning in result.Warnings)
                    {
                        logger.Warn("CheckUpdate: " + warning);
                    }
                }

                LastResults = result?.Updates ?? new List<GameUpdate>();
                LastCheckedAt = DateTime.Now;
                return LastResults;
            }
        }

        /// <summary>
        /// Queues an update through the same install path. butler picks wharf patching on
        /// its own when both ends have build metadata.
        /// </summary>
        public void Start(GameUpdate update)
        {
            if (update?.Game == null || string.IsNullOrEmpty(update.CaveId))
            {
                throw new ArgumentException("Update has no game or cave.", nameof(update));
            }

            var choice = update.Choices?.FirstOrDefault();
            if (choice?.Upload == null)
            {
                throw new Exception("itch.io did not indicate which file to install.");
            }

            var gameId = update.Game.Id.ToString();
            var job = plugin.Installs.Prepare(gameId, choice.Upload, update.CaveId, "update");

            var game = plugin.PlayniteApi.Database.Games
                .FirstOrDefault(g => g.PluginId == plugin.Id && g.GameId == gameId);

            if (plugin.IsUdmAvailable)
            {
                plugin.EnqueueUdmDownload(game ?? new Game(update.Game.Title), job);
            }
            else
            {
                plugin.Installs.RunAsync(job, System.Threading.CancellationToken.None)
                    .ContinueWith(t => logger.Error(t.Exception, "itch.io update failed."),
                        System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }
}
