using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FullDuplexPipe
{
    public interface IFullDuplexPipe
    {
        public void StartListening();
        public void StopListening();
        public Task SendMessageAsync(string message);
    }

    #region Pipe Custom Events
    public class PipeEventArgs : EventArgs
    {
        public byte[] Data { get; protected set; }
        public int Len { get; protected set; }
        public string String { get; protected set; }

        public PipeEventArgs(string str)
        {
            String = str;
        }

        public PipeEventArgs(byte[] data, int len)
        {
            Data = data;
            Len = len;
        }
    }
    #endregion

    #region Full Duplex Base Class
    public abstract class FullDuplexPipe
    {
        public event EventHandler<PipeEventArgs> DataReceived;
        public event EventHandler<EventArgs> PipeClosed;

        public PipeStream pipeStream;
        protected Action<FullDuplexPipe> asyncReaderStart;

        public void Close()
        {
            pipeStream.WaitForPipeDrain();
            pipeStream.Close();
            pipeStream.Dispose();
            pipeStream = null;
        }

        public void StartByteReaderAsync()
        {
            StartByteReaderAsync((b) => DataReceived?.Invoke(this, new PipeEventArgs(b, b.Length)));
        }

        public void StartStringReaderAsync()
        {
            StartByteReaderAsync((b) =>
            {
                string str = Encoding.UTF8.GetString(b).TrimEnd('\0');
                DataReceived?.Invoke(this, new PipeEventArgs(str));
            });
        }

        public void Flush()
        {
            pipeStream.Flush();
        }

        public Task WriteString(string str)
        {
            return WriteBytes(Encoding.UTF8.GetBytes(str));
        }

        public Task WriteBytes(byte[] bytes)
        {
            var blength = BitConverter.GetBytes(bytes.Length);
            var bfull = blength.Concat(bytes).ToArray();
            return pipeStream.WriteAsync(bfull, 0, bfull.Length);
        }

        protected void StartByteReaderAsync(Action<byte[]> packetReceived)
        {
            int intSize = sizeof(int);
            byte[] bDataLength = new byte[intSize];

            pipeStream.ReadAsync(bDataLength, 0, intSize).ContinueWith(t =>
            {
                int len = BitConverter.ToInt32(bDataLength, 0);

                if (len == 0)
                {
                    PipeClosed?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    byte[] data = new byte[len];

                    pipeStream.ReadAsync(data, 0, len).ContinueWith(t2 =>
                    {
                        int readLen = t2.Result;

                        if (readLen == 0)
                        {
                            PipeClosed?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            packetReceived(data);
                            StartByteReaderAsync(packetReceived);
                        }
                    });
                }
            });
        }
    }
    #endregion

    #region Server Pipes
    public class ServerPipe : FullDuplexPipe
    {
        public event EventHandler<EventArgs> Connected;
        public NamedPipeServerStream serverPipeStream;
        private readonly string pipeName;

        public ServerPipe(string pipeName)
        {
            this.pipeName = pipeName;

            serverPipeStream = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            pipeStream = serverPipeStream;
            serverPipeStream.BeginWaitForConnection(PipeConnected, null);
        }

        private void PipeConnected(IAsyncResult ar)
        {
            serverPipeStream.EndWaitForConnection(ar);
            Connected?.Invoke(this, EventArgs.Empty);
            StartStringReaderAsync(); // Start reading messages as soon as connection is established
        }
    }
    public class NamedPipeServer : IFullDuplexPipe
    {
        private const string PipeName = "mypipe";
        private ConcurrentBag<ServerPipe> _clients = new ConcurrentBag<ServerPipe>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly object _clientsLock = new object();

        public async Task SendMessageAsync(string message)
        {
            List<Task> tasks = new List<Task>();
            List<ServerPipe> disconnectedClients = new List<ServerPipe>();

            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    if (client.pipeStream.IsConnected)
                    {
                        tasks.Add(client.WriteString(message).ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                lock (_clientsLock)
                                {
                                    disconnectedClients.Add(client);
                                }
                            }
                        }));
                    }
                    else
                    {
                        disconnectedClients.Add(client);
                    }
                }

                foreach (var client in disconnectedClients)
                {
                    _clients = new ConcurrentBag<ServerPipe>(_clients.Except(new[] { client }));
                }
            }

            await Task.WhenAll(tasks);
        }

        public void StartListening()
        {
            Task.Run(() => ListenForClients(_cancellationTokenSource.Token));
        }

        public void StopListening()
        {
            _cancellationTokenSource.Cancel();
        }

        public async Task ListenForClients(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var serverPipe = new ServerPipe(PipeName);
                serverPipe.Connected += (sender, e) =>
                {
                    lock (_clientsLock)
                    {
                        _clients.Add(serverPipe);
                    }

                    serverPipe.DataReceived += (s, args) =>
                    {
                        Console.Write(args.String.ToString());
                    };
                };

                try
                {
                    await serverPipe.serverPipeStream.WaitForConnectionAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    serverPipe.Close();
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error accepting client connection: {ex.Message}");
                    serverPipe.Close();
                }
            }
        }
    }
    #endregion

    #region Client Pipes
    public class ClientPipe : FullDuplexPipe
    {
        private NamedPipeClientStream clientPipeStream;

        public ClientPipe(string serverName, string pipeName, Action<FullDuplexPipe> asyncReaderStart)
        {
            this.asyncReaderStart = asyncReaderStart;
            clientPipeStream = new NamedPipeClientStream(serverName, pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipeStream = clientPipeStream;
        }

        public void Connect()
        {
            clientPipeStream.Connect();
            asyncReaderStart(this);
        }
    }
    public class NamedPipeClient : IFullDuplexPipe
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private const string PipeName = "mypipe";
        private ClientPipe clientPipe;

        public void StartListening()
        {
            clientPipe = new ClientPipe(".", PipeName, pipe =>
            {
                pipe.DataReceived += (sender, args) =>
                {
                    Console.Write(args.String.ToString());
                };
                pipe.StartStringReaderAsync();
            });

            clientPipe.Connect();
        }

        public void StopListening()
        {
            _cancellationTokenSource.Cancel();
            clientPipe.Close();
        }

        public Task SendMessageAsync(string message)
        {
            return clientPipe.WriteString(message);
        }
    }
    #endregion



}
