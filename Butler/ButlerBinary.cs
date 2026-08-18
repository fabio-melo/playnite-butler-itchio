using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using Playnite.SDK;

namespace ItchioDownloader.Butler
{
    /// <summary>
    /// Finds a butler.exe to run. Preference order:
    ///   1. a copy we downloaded earlier into the extension's data folder;
    ///   2. the one the itch.io app keeps up to date, if the app is installed;
    ///   3. a fresh download from broth, itch.io's own distribution channel.
    ///
    /// Nothing here requires the itch app — it is only a shortcut that saves the
    /// user a download.
    /// </summary>
    public static class ButlerBinary
    {
        private const string BrothRoot = "https://broth.itch.zone/butler";

        private static readonly ILogger logger = LogManager.GetLogger();

        public static string ItchUserPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch");

        /// <summary>
        /// Prereq installers (DirectX, VC++ redists, …) are downloaded here by butler
        /// during Launch. Shared with the itch app when it is installed; harmless if not.
        /// </summary>
        public static string PrereqsPath => Path.Combine(ItchUserPath, "prereqs");

        private static string PlatformSlug =>
            Environment.Is64BitOperatingSystem ? "windows-amd64" : "windows-386";

        /// <summary>
        /// butler.exe shipped with the itch.io app, or empty when the app is absent.
        /// </summary>
        public static string FromItchApp()
        {
            try
            {
                var corePath = Path.Combine(ItchUserPath, "broth", "butler");
                var chosen = Path.Combine(corePath, ".chosen-version");
                if (!File.Exists(chosen))
                {
                    return string.Empty;
                }

                var version = File.ReadAllText(chosen).Trim();
                var exePath = Path.Combine(corePath, "versions", version, "butler.exe");
                return File.Exists(exePath) ? exePath : string.Empty;
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to probe the itch.io app for butler.");
                return string.Empty;
            }
        }

        /// <summary>
        /// The most recent copy we downloaded ourselves, or empty.
        /// </summary>
        public static string FromLocalCache(string dataDir)
        {
            var root = Path.Combine(dataDir, "butler");
            if (!Directory.Exists(root))
            {
                return string.Empty;
            }

            var marker = Path.Combine(root, ".chosen-version");
            if (File.Exists(marker))
            {
                var exePath = Path.Combine(root, File.ReadAllText(marker).Trim(), "butler.exe");
                if (File.Exists(exePath))
                {
                    return exePath;
                }
            }

            foreach (var dir in Directory.GetDirectories(root))
            {
                var exePath = Path.Combine(dir, "butler.exe");
                if (File.Exists(exePath))
                {
                    return exePath;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Resolves a usable butler.exe, downloading one only when neither the cache
        /// nor the itch app can provide it.
        /// </summary>
        public static string Resolve(string dataDir, Action<string> onProgress = null)
        {
            var cached = FromLocalCache(dataDir);
            if (!string.IsNullOrEmpty(cached))
            {
                return cached;
            }

            var fromApp = FromItchApp();
            if (!string.IsNullOrEmpty(fromApp))
            {
                logger.Info("Using butler from the itch.io app: " + fromApp);
                return fromApp;
            }

            return Download(dataDir, onProgress);
        }

        public static string Download(string dataDir, Action<string> onProgress = null)
        {
            onProgress?.Invoke("Checking for the latest butler version…");
            string version;
            using (var web = CreateClient())
            {
                version = web.DownloadString($"{BrothRoot}/{PlatformSlug}/LATEST").Trim();
            }

            if (string.IsNullOrEmpty(version))
            {
                throw new Exception("broth returned an empty butler version.");
            }

            var targetDir = Path.Combine(dataDir, "butler", version);
            var exePath = Path.Combine(targetDir, "butler.exe");
            if (File.Exists(exePath))
            {
                MarkChosen(dataDir, version);
                return exePath;
            }

            onProgress?.Invoke($"Downloading butler {version}…");
            var archive = Path.Combine(Path.GetTempPath(), $"butler-{version}-{Guid.NewGuid():N}.zip");
            try
            {
                using (var web = CreateClient())
                {
                    web.DownloadFile($"{BrothRoot}/{PlatformSlug}/{version}/archive/default", archive);
                }

                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, true);
                }

                Directory.CreateDirectory(targetDir);
                ZipFile.ExtractToDirectory(archive, targetDir);
            }
            finally
            {
                try
                {
                    if (File.Exists(archive))
                    {
                        File.Delete(archive);
                    }
                }
                catch
                {
                    // A leftover temp file is not worth failing the install over.
                }
            }

            if (!File.Exists(exePath))
            {
                throw new Exception($"butler {version} archive did not contain butler.exe.");
            }

            MarkChosen(dataDir, version);
            logger.Info($"Downloaded butler {version} to {targetDir}.");
            return exePath;
        }

        private static void MarkChosen(string dataDir, string version)
        {
            var root = Path.Combine(dataDir, "butler");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".chosen-version"), version);
        }

        private static WebClient CreateClient()
        {
            // broth is TLS-only and .NET 4.6.2 does not negotiate TLS 1.2 by default
            // in every host process.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var web = new WebClient();
            web.Headers.Add("User-Agent", ButlerDaemon.UserAgent);
            return web;
        }
    }
}
