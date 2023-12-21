using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Cynox.IO.Connections
{
	internal static class SocketExtensions
	{
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

			bool blockingState = client.Blocking;
			bool result = false;

			try
			{
				byte[] tmp = new byte[1];
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

			try
			{
				client.Blocking = blockingState;
			}
			catch (SocketException)
			{
				// setting the Blocking property seems to fail if a SocketException was thrown on Send()
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

			const int BytesPerLong = 4; // 32 / 8
			const int BitsPerByte = 8;

			try
			{
				// Array to hold input values.
				var input = new[] {
					timeout == 0 || interval == 0 ? 0UL : 1UL, // on or off
					timeout,
					interval
				};

				// Pack input into byte struct.
				byte[] inValue = new byte[3 * BytesPerLong];
				for (int i = 0; i < input.Length; i++)
				{
					inValue[i * BytesPerLong + 3] = (byte)((input[i] >> ((BytesPerLong - 1) * BitsPerByte)) & 0xff);
					inValue[i * BytesPerLong + 2] = (byte)((input[i] >> ((BytesPerLong - 2) * BitsPerByte)) & 0xff);
					inValue[i * BytesPerLong + 1] = (byte)((input[i] >> ((BytesPerLong - 3) * BitsPerByte)) & 0xff);
					inValue[i * BytesPerLong + 0] = (byte)((input[i] >> ((BytesPerLong - 4) * BitsPerByte)) & 0xff);
				}

				// Create bytestruct for result (bytes pending on server socket).
				byte[] outValue = BitConverter.GetBytes(0);

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