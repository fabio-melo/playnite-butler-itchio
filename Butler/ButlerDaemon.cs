using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace ItchioDownloader.Butler
{
    /// <summary>
    /// One long-lived butlerd process for the whole plugin.
    ///
    /// butlerd is single-tenant: one daemon per database, and the daemon expects a
    /// well-behaved client. Every operation opens its own TCP *conversation* against
    /// that daemon instead of spawning a second one, because notifications only reach
    /// the connection the call was made on.
    /// </summary>
    public class ButlerDaemon : IDisposable
    {
        public const string UserAgent = "playnite-itchio-downloader/0.1.0";

        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly string dataDir;
        private readonly object startLock = new object();

        private Process process;
        private string endpoint;
        private string secret;
        private bool disposed;

        public ButlerDaemon(string dataDir)
        {
            this.dataDir = dataDir;
            Directory.CreateDirectory(dataDir);
        }

        /// <summary>
        /// Our own database. Deliberately not the itch.io app's: butlerd assumes
        /// single-tenant access to it, so sharing would break whenever the app is open.
        /// </summary>
        public string DatabasePath => Path.Combine(dataDir, "butler.db");

        public bool IsRunning => process != null && !process.HasExited;

        public string ExecutablePath { get; private set; }

        /// <summary>
        /// Starts the daemon if it is not up yet. Safe to call from anywhere.
        /// </summary>
        public void EnsureRunning(Action<string> onProgress = null)
        {
            lock (startLock)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(ButlerDaemon));
                }

                if (IsRunning && !string.IsNullOrEmpty(endpoint))
                {
                    return;
                }

                Stop();
                Start(onProgress);
            }
        }

        private void Start(Action<string> onProgress)
        {
            ExecutablePath = ButlerBinary.Resolve(dataDir, onProgress);

            var started = new ManualResetEventSlim(false);
            var startupError = (string)null;

            var args = string.Join(" ",
                "daemon",
                "--json",
                "--transport tcp",
                "--keep-alive",
                $"--dbpath \"{DatabasePath}\"",
                "--address https://itch.io",
                $"--user-agent \"{UserAgent}\"",
                $"--destiny-pid {Process.GetCurrentProcess().Id}");

            process = new Process
            {
                EnableRaisingEvents = true,
                StartInfo = new ProcessStartInfo
                {
                    FileName = ExecutablePath,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(ExecutablePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                {
                    return;
                }

                if (!e.Data.StartsWith("{"))
                {
                    logger.Debug("butlerd: " + e.Data);
                    return;
                }

                try
                {
                    var message = JObject.Parse(e.Data);
                    var type = (string)message["type"];
                    if (type == "butlerd/listen-notification")
                    {
                        endpoint = (string)message["tcp"]?["address"];
                        secret = (string)message["secret"];
                        started.Set();
                    }
                    else if (type == "log")
                    {
                        LogButlerMessage(message);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Unparseable butlerd stdout line: " + e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                {
                    return;
                }

                startupError = e.Data;
                logger.Debug("butlerd (stderr): " + e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!started.Wait(TimeSpan.FromSeconds(20)))
            {
                Stop();
                throw new Exception("butler daemon did not report a listen address. " +
                                    (startupError ?? "No output on stderr."));
            }

            logger.Info($"butlerd listening on {endpoint} (db: {DatabasePath}).");
        }

        private static void LogButlerMessage(JObject message)
        {
            var text = (string)message["message"];
            switch ((string)message["level"])
            {
                case "error":
                    logger.Error("butlerd: " + text);
                    break;
                case "warning":
                    logger.Warn("butlerd: " + text);
                    break;
                default:
                    logger.Debug("butlerd: " + text);
                    break;
            }
        }

        /// <summary>
        /// Opens an authenticated connection. Every long-running call should get its
        /// own, because butlerd delivers a call's notifications on the connection that
        /// issued it.
        /// </summary>
        public ButlerConversation OpenConversation(Action<string> onProgress = null)
        {
            EnsureRunning(onProgress);
            return new ButlerConversation(endpoint, secret);
        }

        private void Stop()
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to stop butlerd.");
            }
            finally
            {
                process.Dispose();
                process = null;
                endpoint = null;
                secret = null;
            }
        }

        public void Dispose()
        {
            lock (startLock)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (process != null && !process.HasExited && !string.IsNullOrEmpty(endpoint))
                {
                    try
                    {
                        using (var conversation = new ButlerConversation(endpoint, secret))
                        {
                            conversation.Client.Send("Meta.Shutdown");
                        }

                        process.WaitForExit(5000);
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e, "butlerd did not shut down gracefully.");
                    }
                }

                Stop();
            }
        }
    }

    /// <summary>
    /// An authenticated connection to butlerd. Dispose it when the call it was opened
    /// for is done — notification handlers live and die with it.
    /// </summary>
    public class ButlerConversation : IDisposable
    {
        public JsonRpcClient Client { get; }

        public ButlerConversation(string endpoint, string secret)
        {
            Client = new JsonRpcClient(endpoint);
            try
            {
                // Meta.Authenticate must be the first request on every connection.
                Client.Send<JToken>("Meta.Authenticate", new { secret });
            }
            catch
            {
                Client.Dispose();
                throw;
            }
        }

        public void Dispose() => Client.Dispose();
    }
}
