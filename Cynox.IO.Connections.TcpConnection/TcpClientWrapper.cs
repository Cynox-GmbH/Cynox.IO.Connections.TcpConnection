using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Timer = System.Timers.Timer;

namespace Cynox.IO.Connections
{
    /// <summary>
    /// Wrapper around the TcpClient class that simplifies usage when sending and receiving data
    /// from a connected socket. Provides automatic reconnect.
    /// </summary>
    internal class TcpClientWrapper : IDisposable
    {
        public event Action<TcpClientWrapperDataReceivedEventArgs> DataReceived;

        private const int DEFAULT_CHECK_CONNECTION_INTERVAL = 5000;
        private const int DEFAULT_TRY_RECONNECT_INTERVAL = 30000;

        private TcpClient _Client;
        private readonly byte[] _Buffer;
        private readonly Timer _CheckConnectionTimer;
        private CancellationTokenSource _ReceiveDataTaskCts = new CancellationTokenSource();
        private Task _ReceiveDataTask;
        
        #region Public Properties

        public IPAddress IpAddress { get; set; }
        public int Port { get; set; }
        public TimeSpan TryReconnectInterval { get; set; } = TimeSpan.FromMilliseconds(DEFAULT_TRY_RECONNECT_INTERVAL);
        public TimeSpan CheckConnectionInterval { get; set; } = TimeSpan.FromMilliseconds(DEFAULT_CHECK_CONNECTION_INTERVAL);

        /// <summary>
        /// Checks if the underlying Socket is still connected.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                if (_Client?.Client is Socket socket)
                {
                    return socket.IsConnectedMsdn();
                }

                return false;
            }
        }

        /// <summary>
        /// Returns the underlying <see cref="TcpClient"/>.
        /// </summary>
        public TcpClient Client => _Client;

        #endregion

        public TcpClientWrapper(IPAddress address, int port, int bufferSize = 2048)
        {
            IpAddress = address ?? throw new ArgumentNullException(nameof(address));
            Port = port;
            _Buffer = new byte[bufferSize];

            _CheckConnectionTimer = new Timer(DEFAULT_CHECK_CONNECTION_INTERVAL);
            _CheckConnectionTimer.Elapsed += CheckConnectionTimer_OnElapsed;

            _Client = new TcpClient();
        }

        public TcpClientWrapper(string ipAddress, int port) : this(IPAddress.Parse(ipAddress), port)
        {
        }

        /// <summary>
        /// Sends data to a connected Socket using the specified SocketFlags.
        /// </summary>
        /// <param name="data">Data to be sent.</param>
        /// <param name="blocking">Specifies if the Socket should be set to blocking mode.</param>
        /// <param name="flags">A bitwise combination of the SocketFlags values.</param>
        /// <returns>The number of bytes sent to the Socket.</returns>
        /// <exception cref="SocketException"></exception>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ObjectDisposedException"></exception>
        public int Send(List<byte> data, bool blocking = true, SocketFlags flags = SocketFlags.None)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (_Client?.Client != null)
            {
                _Client.Client.Blocking = blocking;
                return _Client.Client.Send(data.ToArray(), flags);
            }

            return 0;
        }
        
        /// <summary>
        /// Performs a request for a remote host connection.
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns></returns>
        /// <exception cref="Exception">Throws an exception if connection fails.</exception>
        public void Connect(int timeout = 1000)
        {
            Disconnect();

            try
            {
                _Client = new TcpClient();

                // Use async connect, to be able to specify timeout.
                var asyncResult = _Client.BeginConnect(IpAddress.ToString(), Port, null, null);
                asyncResult.AsyncWaitHandle.WaitOne(timeout);

                if (!_Client.Connected)
                {
                    throw new Exception($"Failed to connect to {IpAddress}:{Port}");
                }

                _Client.EndConnect(asyncResult);
                _Client.Client?.SetKeepAlive(2000, 500);

                StartReceiveDataTask();
                _CheckConnectionTimer.Start();
            }
            catch (Exception)
            {
                _Client?.Close();
                throw;
            }
        }

        /// <summary>
        /// Closes the socket connection and allows reuse of the socket.
        /// Disposes the underlying TcpClient instance and requests that the underlying TCP connection be closed.
        /// </summary>
        public void Disconnect()
        {
            _CheckConnectionTimer.Stop();
            StopReceiveDataTask();

            if (_Client?.Client == null)
            {
                return;
            }

            try
            {
                var socket = _Client.Client;
                socket.Disconnect(false);
                _Client?.Close();
            }
            catch (SocketException)
            {
                // client not connected
            }
            catch (ObjectDisposedException)
            {
                // client bereits zerstört
            }
        }

        private void StartReceiveDataTask()
        {
            // Return if task is still running
            if (_ReceiveDataTask != null)
            {
                if (!_ReceiveDataTask.IsCompleted) {
                    return;
                }
            }

            _ReceiveDataTaskCts = new CancellationTokenSource();

            // Continuously read data from the server until the connection is closed or an error occurs.
            _ReceiveDataTask = Task.Factory.StartNew(async () =>
            {
                try
                {
                    var stream = _Client.GetStream();
                    int bytesRead;

                    while ((bytesRead = await stream.ReadAsync(_Buffer, 0, _Buffer.Length, _ReceiveDataTaskCts.Token)) > 0)
                    {
                        if (_ReceiveDataTaskCts.IsCancellationRequested)
                        {
                            break;
                        }

                        var receivedData = new byte[bytesRead];
                        Array.Copy(_Buffer, receivedData, bytesRead);
                        OnDataReceived(new TcpClientWrapperDataReceivedEventArgs(new List<byte>(receivedData)));
                    }
                }
                catch (IOException)
                { }
                catch (ObjectDisposedException)
                { }
                finally
                {
                    _ReceiveDataTaskCts.Cancel();
                }
            }, _ReceiveDataTaskCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void StopReceiveDataTask()
        {
            _ReceiveDataTaskCts.Cancel();

            try
            {
                _ReceiveDataTask?.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Ignore exception since the task has completed anyway
            }
        }

        private void OnDataReceived(TcpClientWrapperDataReceivedEventArgs args)
        {
            var handler = DataReceived;
            handler?.Invoke(args);
        }

        private void CheckConnectionTimer_OnElapsed(object sender, ElapsedEventArgs e)
        {
            _CheckConnectionTimer.Stop();

            try
            {
                if (IsConnected)
                {
                    _CheckConnectionTimer.Interval = CheckConnectionInterval.TotalMilliseconds;
                }
                else
                {
                    // Make sure connection is really closed.
                    Disconnect();

                    try
                    {
                        Connect();
                        // Re-connect successful, switch back to CheckConnectionInterval.
                        _CheckConnectionTimer.Interval = CheckConnectionInterval.TotalMilliseconds;
                    }
                    catch
                    {
                        // Re-connect failed, switch to TryReconnectInterval.
                        _CheckConnectionTimer.Interval = TryReconnectInterval.TotalMilliseconds;
                    }
                }
            }
            catch (Exception)
            {
                // If something does wrong, the reconnection timer will be restarted anyway.
            }
            finally
            {
                _CheckConnectionTimer.Start();
            }
        }

        #region IDisposable

        // Flag: Has Dispose already been called?
        private bool _Disposed;

        // Public implementation of Dispose pattern callable by consumers.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (_Disposed)
            {
                return;
            }

            if (disposing)
            {
                Disconnect();
                _Client?.Dispose();
                _CheckConnectionTimer?.Stop();
                _CheckConnectionTimer?.Dispose();
                _ReceiveDataTask?.Dispose();
                _ReceiveDataTaskCts?.Dispose();
            }

            // Free any unmanaged objects here.
            _Disposed = true;
        }

        #endregion
    }
}
