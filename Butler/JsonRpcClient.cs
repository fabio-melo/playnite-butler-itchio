using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;

namespace ItchioDownloader.Butler
{
    /// <summary>
    /// Raised for a notification (a message with a method and no id).
    /// </summary>
    public class RpcNotificationEventArgs : EventArgs
    {
        public string Method { get; set; }
        public JToken Params { get; set; }

        public T GetParams<T>() => Params == null ? default(T) : Params.ToObject<T>();
    }

    /// <summary>
    /// Raised when butlerd asks the client something mid-call (PickManifestAction,
    /// AcceptLicense, ShellLaunch, …). The handler must call Respond or RespondError.
    /// </summary>
    public class RpcServerRequestEventArgs : EventArgs
    {
        public long Id { get; set; }
        public string Method { get; set; }
        public JToken Params { get; set; }

        public T GetParams<T>() => Params == null ? default(T) : Params.ToObject<T>();
    }

    public class RpcException : Exception
    {
        public int Code { get; }

        /// <summary>The error's `data` payload. Named apart from Exception.Data.</summary>
        public JToken ErrorData { get; }

        public RpcException(int code, string message, JToken data) : base(message)
        {
            Code = code;
            ErrorData = data;
        }

        /// <summary>
        /// butlerd wraps itch.io API failures in data.apiError.messages, and those
        /// strings are already written for end users.
        /// </summary>
        public string UserMessage
        {
            get
            {
                var messages = ErrorData?["apiError"]?["messages"] as JArray;
                if (messages != null && messages.Count > 0)
                {
                    return string.Join(Environment.NewLine, messages);
                }

                return Message;
            }
        }
    }

    /// <summary>
    /// JSON-RPC 2.0 over a raw TCP socket, one message per line terminated by \n.
    /// No headers, no HTTP framing — that is the whole butlerd wire protocol.
    /// </summary>
    public class JsonRpcClient : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly JsonSerializerSettings serializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly StreamWriter writer;
        private readonly object writeLock = new object();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> pending =
            new ConcurrentDictionary<long, TaskCompletionSource<JObject>>();
        private readonly CancellationTokenSource readerCts = new CancellationTokenSource();

        private long nextId;
        private bool disposed;

        public event EventHandler<RpcNotificationEventArgs> NotificationReceived;
        public event EventHandler<RpcServerRequestEventArgs> RequestReceived;

        public JsonRpcClient(string address)
        {
            var separator = address.LastIndexOf(':');
            if (separator < 0)
            {
                throw new ArgumentException("Address must be host:port.", nameof(address));
            }

            var host = address.Substring(0, separator);
            var port = int.Parse(address.Substring(separator + 1));

            client = new TcpClient();
            client.Connect(host, port);
            stream = client.GetStream();
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            Task.Run(() => ReadLoop());
        }

        private async Task ReadLoop()
        {
            try
            {
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    while (!readerCts.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        if (line.Length == 0)
                        {
                            continue;
                        }

                        try
                        {
                            Dispatch(JObject.Parse(line));
                        }
                        catch (Exception e)
                        {
                            logger.Error(e, "Failed to dispatch butlerd message: " + line);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (!disposed)
                {
                    logger.Error(e, "butlerd connection dropped.");
                }
            }
            finally
            {
                FailPending(new RpcException(0, "butlerd connection closed.", null));
            }
        }

        private void Dispatch(JObject message)
        {
            var id = message["id"];
            var method = (string)message["method"];

            if (id != null && method == null)
            {
                // Response to one of our requests.
                var key = id.Value<long>();
                if (pending.TryRemove(key, out var completion))
                {
                    completion.TrySetResult(message);
                }
                else
                {
                    logger.Warn($"butlerd response for unknown request {key}.");
                }
            }
            else if (id != null)
            {
                // Server-to-client request; the handler owes butlerd a response.
                RequestReceived?.Invoke(this, new RpcServerRequestEventArgs
                {
                    Id = id.Value<long>(),
                    Method = method,
                    Params = message["params"]
                });
            }
            else if (method != null)
            {
                NotificationReceived?.Invoke(this, new RpcNotificationEventArgs
                {
                    Method = method,
                    Params = message["params"]
                });
            }
            else
            {
                logger.Error("Invalid butlerd message: " + message.ToString(Formatting.None));
            }
        }

        private void FailPending(Exception error)
        {
            foreach (var key in pending.Keys)
            {
                if (pending.TryRemove(key, out var completion))
                {
                    completion.TrySetException(error);
                }
            }
        }

        private void WriteLine(string payload)
        {
            lock (writeLock)
            {
                writer.Write(payload);
                writer.Write('\n');
                writer.Flush();
            }
        }

        public async Task<JToken> SendAsync(string method, object parameters, CancellationToken token = default(CancellationToken))
        {
            var id = Interlocked.Increment(ref nextId);
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;

            var request = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters == null ? new JObject() : JToken.FromObject(parameters, JsonSerializer.Create(serializerSettings))
            };

            using (token.Register(() =>
            {
                if (pending.TryRemove(id, out var cancelled))
                {
                    cancelled.TrySetCanceled();
                }
            }))
            {
                WriteLine(request.ToString(Formatting.None));
                var response = await completion.Task.ConfigureAwait(false);

                var error = response["error"] as JObject;
                if (error != null)
                {
                    throw new RpcException(
                        error["code"]?.Value<int>() ?? 0,
                        (string)error["message"] ?? "butlerd call failed.",
                        error["data"]);
                }

                return response["result"];
            }
        }

        public async Task<T> SendAsync<T>(string method, object parameters, CancellationToken token = default(CancellationToken))
        {
            var result = await SendAsync(method, parameters, token).ConfigureAwait(false);
            return result == null ? default(T) : result.ToObject<T>();
        }

        public T Send<T>(string method, object parameters = null)
        {
            return SendAsync<T>(method, parameters).GetAwaiter().GetResult();
        }

        public void Send(string method, object parameters = null)
        {
            SendAsync(method, parameters).GetAwaiter().GetResult();
        }

        public void Respond(long id, object result)
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result == null
                    ? new JObject()
                    : JToken.FromObject(result, JsonSerializer.Create(serializerSettings))
            };

            WriteLine(response.ToString(Formatting.None));
        }

        public void RespondError(long id, int code, string message)
        {
            var response = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };

            WriteLine(response.ToString(Formatting.None));
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            readerCts.Cancel();
            NotificationReceived = null;
            RequestReceived = null;

            try
            {
                stream.Close();
            }
            catch
            {
                // The socket is going away either way.
            }

            client.Close();
            readerCts.Dispose();
        }
    }
}
