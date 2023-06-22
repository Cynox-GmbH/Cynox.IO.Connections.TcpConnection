using System.Collections.Generic;

namespace Cynox.IO.Connections
{
	internal class TcpClientWrapperDataReceivedEventArgs
	{
		public List<byte> Data { get; }

		public TcpClientWrapperDataReceivedEventArgs(List<byte> data)
		{
			Data = data;
		}
	}
}