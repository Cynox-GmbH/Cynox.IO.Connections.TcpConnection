using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Cynox.IO.Connections
{
    /// <summary>
    /// <see cref="IConnection"/> to be used for TCP connections.
    /// </summary>
    public class TcpConnection : IConnection
    {
        private readonly TcpClientWrapper _Client;

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        /// <param name="ipAddress">The target IP-address</param>
        /// <param name="port">The target port</param>
        /// <exception cref="ArgumentNullException"><paramref name="ipAddress"></paramref> is null.</exception>
        public TcpConnection(IPAddress ipAddress, int port)
        {
            if (ipAddress == null)
            {
                throw new ArgumentNullException(nameof(ipAddress));
            }

            _Client = new TcpClientWrapper(ipAddress, port);
            _Client.DataReceived += ClientOnDataReceived;
        }

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        /// <param name="ipAddress">The target IP-address</param>
        /// <param name="port">The target port</param>
        /// <exception cref="ArgumentNullException"><paramref name="ipAddress"></paramref> is null.</exception>
        /// <exception cref="FormatException"><paramref name="ipAddress"></paramref> is not valid.</exception>
        public TcpConnection(string ipAddress, int port) : this(IPAddress.Parse(ipAddress), port)
        {
        }

        /// <summary>
        /// Gets or sets the current target IP address.
        /// </summary>
        /// <exception cref="ArgumentNullException">Value is null.</exception>
        public IPAddress IpAddress {
            get => _Client.IpAddress;
            set => _Client.IpAddress = value ?? throw new ArgumentNullException();
        }

        /// <summary>
        /// Gets or sets the current target port.
        /// </summary>
        public int Port
        {
            get => _Client.Port;
            set => _Client.Port = value;
        }

        /// <summary>
        /// Returns the underlying <see cref="TcpClient"/>.
        /// </summary>
        /// <remarks>The client may have already been disposed if the connection is closed.</remarks>
        public TcpClient Client => _Client?.Client;

        private void ClientOnDataReceived(TcpClientWrapperDataReceivedEventArgs args)
        {
            OnDataReceived(args.Data);
        }

        private void OnDataReceived(IList<byte> data)
        {
            var handler = DataReceived;
            handler?.Invoke(this, new ConnectionDataReceivedEventArgs(data));
        }

        #region IModControlConnection

        /// <inheritdoc />
        public event Action<object, ConnectionDataReceivedEventArgs> DataReceived;

        /// <summary>
        /// Performs a request for a remote host connection.
        /// This creates a new instance of the underlying <see cref="TcpClient"/>.
        /// </summary>
        /// <exception cref="ConnectionException"></exception>
        public void Connect()
        {
            try
            {
                _Client.Connect();
            }
            catch (Exception ex)
            {
                throw new ConnectionException("TcpClient failed to connect", ex);
            }
        }

        /// <summary>
        /// Closes the socket connection and allows reuse of the socket.
        /// Disposes the underlying TcpClient instance and requests that the underlying TCP connection be closed. 
        /// </summary>
        /// <exception cref="ConnectionException"></exception>
        public void Disconnect()
        {
            try
            {
                _Client.Disconnect();
            }
            catch (Exception ex)
            {
                throw new ConnectionException("An error occurred while disconnecting", ex);
            }
        }

        /// <summary>
        /// Checks if the underlying Socket is still connected.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                if (_Client != null)
                {
                    return _Client.IsConnected;
                }

                return false;
            }
        }

        /// <inheritdoc />
        public void Send(IList<byte> data)
        {
            if (data == null || !data.Any())
            {
                return;
            }

            try
            {
                _Client?.Send(data.ToList());
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                throw new ConnectionException("", ex);
            }
        }

        /// <inheritdoc cref="IConnection"/>
        public override string ToString()
        {
            return $"{IpAddress}:{Port}";
        }

        #endregion

        #region IDisposable

        private bool _Disposed;

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected implementation of Dispose pattern.
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (_Disposed)
            {
                return;
            }

            if (disposing)
            {
                Disconnect();
                _Client.Dispose();
            }

            _Disposed = true;
        }

        #endregion
    }

}
