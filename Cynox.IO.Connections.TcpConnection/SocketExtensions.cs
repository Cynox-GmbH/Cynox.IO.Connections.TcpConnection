using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Cynox.IO.Connections
{
	internal static class SocketExtensions
	{
        /// <summary>
        /// Polls the connection to check if the connection is still active.
        /// Also checks the Socket.Connected property in case the socket has not been initialized in the first place.
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static bool IsConnectedPoll(this Socket s)
        {
            try
            {
                return !((s.Poll(1000, SelectMode.SelectRead) && s.Available == 0) || !s.Connected);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
		
        /// <summary>
        /// Checks if the Socket is still connected by performing a non-blocking, zero-byte Send() call.
        /// </summary>
        /// <param name="client"></param>
        /// <returns>true if connected, otherwise false</returns>
        public static bool IsConnectedMsdn(this Socket client)
		{
			// From MSDN:
			// The Connected property gets the connection state of the Socket as of the last I/O operation.
			// When it returns false, the Socket was either never connected, or is no longer connected.
			// The value of the Connected property reflects the state of the connection as of the most recent operation.

			// If you need to determine the current state of the connection, make a nonblocking, zero - byte Send call.
			// If the call returns successfully or throws a WAEWOULDBLOCK error code(10035), then the socket is still connected; otherwise, the socket is no longer connected.

			// If you call Connect on a User Datagram Protocol(UDP) socket, the Connected property always returns true;
			// however, this action does not change the inherent connectionless nature of UDP.

			var blockingState = client.Blocking;
			var result = false;

            try
            {
                var tmp = new byte[1];
                client.Blocking = false;
                client.Send(tmp, 0, 0);
                result = true;
            }
            catch (SocketException e)
            {
                // 10035 == WSAEWOULDBLOCK
                if (e.NativeErrorCode.Equals(10035))
                {
                    result = true;
                }
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

			try
			{
				client.Blocking = blockingState;
			}
			catch (SocketException)
			{
				// setting the Blocking property seems to fail if a SocketException was thrown on Send()
			}
            catch (ObjectDisposedException)
            {
                return false;
            }

            return result;
		}

		/// <summary>
		/// Sets the keep-alive interval for the socket.
		/// 
		/// The socket connection is considered to be dead after (timeout + 10 * interval) milliseconds.
		/// After that, any read/write or poll operations should fail on the socket.
		/// </summary>
		/// <param name="socket">The socket.</param>
		/// <param name="timeout">Time between two keep alive "pings".</param>
		/// <param name="interval">Time between two keep alive "pings" when first one fails. Repeated 10 times (hardcoded since Vista?!).</param>
		/// <returns>true if the keep alive infos were succefully modified.</returns>
		public static bool SetKeepAlive(this Socket socket, ulong timeout, ulong interval)
		{
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				return false;
			}

			const int bytesPerLong = 4; // 32 / 8
			const int bitsPerByte = 8;

			try
			{
				// Array to hold input values.
				var input = new[] {
					timeout == 0 || interval == 0 ? 0UL : 1UL, // on or off
					timeout,
					interval
				};

				// Pack input into byte struct.
				var inValue = new byte[3 * bytesPerLong];
				for (var i = 0; i < input.Length; i++)
				{
					inValue[i * bytesPerLong + 3] = (byte)((input[i] >> ((bytesPerLong - 1) * bitsPerByte)) & 0xff);
					inValue[i * bytesPerLong + 2] = (byte)((input[i] >> ((bytesPerLong - 2) * bitsPerByte)) & 0xff);
					inValue[i * bytesPerLong + 1] = (byte)((input[i] >> ((bytesPerLong - 3) * bitsPerByte)) & 0xff);
					inValue[i * bytesPerLong + 0] = (byte)((input[i] >> ((bytesPerLong - 4) * bitsPerByte)) & 0xff);
				}

				// Create bytestruct for result (bytes pending on server socket).
				var outValue = BitConverter.GetBytes(0);

				// Write SIO_VALS to Socket IOControl.
				socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
				socket.IOControl(IOControlCode.KeepAliveValues, inValue, outValue);
			}
			catch (SocketException)
			{
				return false;
			}

			return true;
		}
	}
}