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

        private const int DefaultCheckConnectionInterval = 5000;
        private const int DefaultTryReconnectInterval = 30000;

        private TcpClient _client;
        private readonly byte[] _buffer;
        private readonly Timer _checkConnectionTimer;
        private CancellationTokenSource _receiveDataTaskCts = new CancellationTokenSource();
        private Task _receiveDataTask;
        private TimeSpan _tryReconnectInterval = TimeSpan.FromMilliseconds(DefaultTryReconnectInterval);
        private TimeSpan _checkConnectionInterval = TimeSpan.FromMilliseconds(DefaultCheckConnectionInterval);
        private bool _autoReconnect;

        #region Public Properties

        public IPAddress IpAddress { get; set; }
        public int Port { get; set; }
        
        /// <summary>
        /// Specifies the interval in which an attempt is made to reestablish a broken connection.
        /// Range: 1s to 120s.
        /// </summary>
        public TimeSpan TryReconnectInterval
        {
            get => _tryReconnectInterval;
            set
            {
                if (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromSeconds(120))
                {
                    throw new ArgumentOutOfRangeException(nameof(TryReconnectInterval));
                }
                
                _tryReconnectInterval = value;
            }
        }

        /// <summary>
        /// Specifies the interval in which a check is made to see if the client is still connected.
        /// Range: 100ms to 10s.
        /// </summary>
        public TimeSpan CheckConnectionInterval
        {
            get => _checkConnectionInterval;
            set
            {
                if (value < TimeSpan.FromMilliseconds(100) && value > TimeSpan.FromSeconds(10))
                {
                    throw new ArgumentOutOfRangeException(nameof(CheckConnectionInterval));
                }
                
                _checkConnectionInterval = value;
            }
        }

        /// <summary>
        /// Checks if the underlying Socket is still connected.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                if (_client?.Client is Socket socket)
                {
                    return socket.IsConnectedPoll() && socket.IsConnectedMsdn();
                }

                return false;
            }
        }

        /// <summary>
        /// Returns the underlying <see cref="TcpClient"/>.
        /// </summary>
        public TcpClient Client => _client;

        #endregion

        public TcpClientWrapper(IPAddress address, int port, int bufferSize = 2048)
        {
            IpAddress = address ?? throw new ArgumentNullException(nameof(address));
            Port = port;
            _buffer = new byte[bufferSize];

            _checkConnectionTimer = new Timer(CheckConnectionInterval.TotalMilliseconds);
            _checkConnectionTimer.Elapsed += CheckConnectionTimer_OnElapsed;
            
            _client = new TcpClient();
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

            if (_client?.Client == null)
            {
                return 0;
            }
            
            _client.Client.Blocking = blocking;
            return _client.Client.Send(data.ToArray(), flags);

        }

        /// <summary>
        /// Performs a request for a remote host connection.
        /// </summary>
        /// <param name="autoReconnect">Automatically try to reconnect, if connection was interrupted. Only applies, if initial connection was successful.</param>
        /// <param name="reconnectOnFail">Automatically try to reconnect, even if the initial connection fails. Requires <see cref="autoReconnect"/> to be true.</param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        /// <exception cref="Exception">Throws an exception if connection fails.</exception>
        public void Connect(bool autoReconnect, bool reconnectOnFail = false, int timeout = 1000)
        {
            _autoReconnect = autoReconnect;
            Disconnect();

            try
            {
                _client = new TcpClient();

                // Use async connect, to be able to specify timeout.
                var asyncResult = _client.BeginConnect(IpAddress.ToString(), Port, null, null);
                asyncResult.AsyncWaitHandle.WaitOne(timeout);

                if (!_client.Connected)
                {
                    if (reconnectOnFail)
                    {
                        _checkConnectionTimer.Interval = TryReconnectInterval.TotalMilliseconds;
                        _checkConnectionTimer.Enabled = _autoReconnect;
                    }

                    throw new Exception($"Failed to connect to {IpAddress}:{Port}");
                }

                _client.EndConnect(asyncResult);
                _client.Client?.SetKeepAlive(2000, 500);

                StartReceiveDataTask();
                _checkConnectionTimer.Interval = CheckConnectionInterval.TotalMilliseconds;
                _checkConnectionTimer.Enabled = _autoReconnect;
            }
            catch (Exception)
            {
                _client?.Close();
                throw;
            }
        }

        /// <summary>
        /// Closes the socket connection and allows reuse of the socket.
        /// Disposes the underlying TcpClient instance and requests that the underlying TCP connection be closed.
        /// </summary>
        public void Disconnect()
        {
            _checkConnectionTimer.Stop();
            StopReceiveDataTask();

            if (_client?.Client == null)
            {
                return;
            }

            try
            {
                var socket = _client.Client;
                socket.Disconnect(false);
                _client?.Close();
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
            if (_receiveDataTask != null)
            {
                if (!_receiveDataTask.IsCompleted) {
                    return;
                }
            }

            _receiveDataTaskCts = new CancellationTokenSource();

            // Continuously read data from the server until the connection is closed or an error occurs.
            _receiveDataTask = Task.Factory.StartNew(async () =>
            {
                try
                {
                    var stream = _client.GetStream();
                    int bytesRead;

                    while ((bytesRead = await stream.ReadAsync(_buffer, 0, _buffer.Length, _receiveDataTaskCts.Token)) > 0)
                    {
                        if (_receiveDataTaskCts.IsCancellationRequested)
                        {
                            break;
                        }

                        var receivedData = new byte[bytesRead];
                        Array.Copy(_buffer, receivedData, bytesRead);
                        OnDataReceived(new TcpClientWrapperDataReceivedEventArgs(new List<byte>(receivedData)));
                    }
                }
                catch (IOException)
                { }
                catch (ObjectDisposedException)
                { }
                finally
                {
                    _receiveDataTaskCts.Cancel();
                }
            }, _receiveDataTaskCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void StopReceiveDataTask()
        {
            _receiveDataTaskCts.Cancel();

            try
            {
                _receiveDataTask?.GetAwaiter().GetResult();
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
            _checkConnectionTimer.Stop();

            try
            {
                if (IsConnected)
                {
                    _checkConnectionTimer.Interval = CheckConnectionInterval.TotalMilliseconds;
                }
                else
                {
                    // Make sure connection is really closed.
                    Disconnect();

                    try
                    {
                        Connect(_autoReconnect);
                        // Re-connect successful, switch back to CheckConnectionInterval.
                        _checkConnectionTimer.Interval = CheckConnectionInterval.TotalMilliseconds;
                    }
                    catch
                    {
                        // Re-connect failed, switch to TryReconnectInterval.
                        _checkConnectionTimer.Interval = TryReconnectInterval.TotalMilliseconds;
                    }
                }
            }
            catch (Exception)
            {
                // If something does wrong, the reconnection timer will be restarted anyway.
            }
            finally
            {
                _checkConnectionTimer.Enabled = _autoReconnect;
            }
        }

        #region IDisposable

        // Flag: Has Dispose() already been called?
        private bool _disposed;

        // Public implementation of Dispose pattern callable by consumers.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                Disconnect();
                _client?.Dispose();
                _checkConnectionTimer?.Stop();
                _checkConnectionTimer?.Dispose();
                _receiveDataTask?.Dispose();
                _receiveDataTaskCts?.Dispose();
            }

            // Free any unmanaged objects here.
            _disposed = true;
        }

        #endregion
    }
}
