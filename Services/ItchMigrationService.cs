using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace ItchioDownloader.Services
{
    public class MigrationResult
    {
        public int Moved { get; set; }
        public int Skipped { get; set; }
        public int Candidates { get; set; }
    }

    /// <summary>
    /// Moves library entries between Playnite's built-in itch.io plugin and this one.
    ///
    /// Same approach the GOG OSS / Legendary plugins use for their built-in
    /// counterparts: flip Game.PluginId in place instead of re-importing, so playtime,
    /// tags, completion status, custom art and play sessions all survive. It works here
    /// because both plugins key games by the itch.io game id.
    /// </summary>
    public class ItchMigrationService
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Playnite's built-in "itch.io library integration".</summary>
        public static readonly Guid BuiltinItchioPluginId = Guid.Parse("00000001-EBB2-4EEC-ABCB-7C89937A42BB");

        private readonly ItchioDownloaderPlugin plugin;

        public ItchMigrationService(ItchioDownloaderPlugin plugin)
        {
            this.plugin = plugin;
        }

        private IPlayniteAPI Api => plugin.PlayniteApi;

        public int CountMigratable() => Api.Database.Games.Count(g => g.PluginId == BuiltinItchioPluginId);

        public int CountRevertable() => Api.Database.Games.Count(g => g.PluginId == plugin.Id);

        public MigrationResult Migrate(Action<int, int> onProgress = null) =>
            Move(BuiltinItchioPluginId, plugin.Id, onProgress);

        public MigrationResult Revert(Action<int, int> onProgress = null) =>
            Move(plugin.Id, BuiltinItchioPluginId, onProgress);

        private MigrationResult Move(Guid from, Guid to, Action<int, int> onProgress)
        {
            var result = new MigrationResult();

            using (Api.Database.BufferedUpdate())
            {
                var candidates = Api.Database.Games.Where(g => g.PluginId == from).ToList();
                result.Candidates = candidates.Count;

                // Duplicates are the one thing a straight id flip can create, so the
                // destination side is checked up front rather than per game.
                var occupied = new HashSet<string>(
                    Api.Database.Games
                        .Where(g => g.PluginId == to && g.GameId != null)
                        .Select(g => g.GameId),
                    StringComparer.OrdinalIgnoreCase);

                var index = 0;
                var moved = new List<Game>();

                foreach (var game in candidates)
                {
                    index++;
                    onProgress?.Invoke(index, candidates.Count);

                    if (game.GameId == null || occupied.Contains(game.GameId))
                    {
                        result.Skipped++;
                        continue;
                    }

                    game.PluginId = to;
                    occupied.Add(game.GameId);
                    moved.Add(game);
                }

                if (moved.Count > 0)
                {
                    Api.Database.Games.Update(moved);
                    result.Moved = moved.Count;
                }
            }

            logger.Info($"Moved {result.Moved} game(s) from {from} to {to} " +
                        $"({result.Skipped} skipped of {result.Candidates}).");
            return result;
        }
    }
}
