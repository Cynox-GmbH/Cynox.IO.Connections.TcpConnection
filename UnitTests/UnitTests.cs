using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cynox.IO.Connections;

namespace UnitTests
{
    public class Tests
    {
        const int TestPort = 11000;
        
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void InvalidConstructionTests()
        {
            // ReSharper disable once ObjectCreationAsStatement
            Assert.Throws<FormatException>(() => new TcpConnection("", 1470), "Invalid IP-Address should throw");
        }

        [Test]
        public void ConstructionTests()
        {
            const string ipAddress = "192.168.178.1";
            const int port = 1470;

            var c = new TcpConnection(ipAddress, port);
            
            Assert.That(c.IpAddress.ToString(), Is.EqualTo(ipAddress));
            Assert.That(c.Port, Is.EqualTo(port));
            Assert.That(c.Client, Is.Not.Null);
            Assert.DoesNotThrow(() => c.Disconnect());
            Assert.DoesNotThrow(() => c.Dispose());
        }
        
        [Test]
        public void ConnectToInvalidTargetTest()
        {
            var c = new TcpConnection("1.2.3.4", 1234);

            Assert.Throws<ConnectionException>(() => c.Connect());
            Assert.That(!c.IsConnected);
            Assert.DoesNotThrow(() => c.Dispose());
        }

        [Test]
        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        public void ShouldNotReconnectIfInitialConnectionFails()
        {
            var cts = new CancellationTokenSource();
            var c = new TcpConnection(IPAddress.Loopback, TestPort)
                       {
                           CheckConnectionInterval = TimeSpan.FromMilliseconds(500),
                           TryReconnectInterval = TimeSpan.FromMilliseconds(1000),
                           _autoReconnect = true
                       };
            
            Assert.Throws<ConnectionException>(() => c.Connect());
            Assert.That(() => c.IsConnected, Is.False, "Should be unable to connect");
            var listenerTask = ListenAndRespondAsync("", cts.Token);
            Assert.That(() => c.IsConnected, Is.False.After(5000, 100), "Connect failed");
            cts.Cancel();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await listenerTask);
            cts.Dispose();
            c.Dispose();
        }

        [Test]
        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        public void ShouldReconnectAfterDisconnect()
        {
            var cts = new CancellationTokenSource();
            var c = new TcpConnection(IPAddress.Loopback, TestPort)
                    {
                        CheckConnectionInterval = TimeSpan.FromMilliseconds(500),
                        TryReconnectInterval = TimeSpan.FromMilliseconds(1000),
                        _autoReconnect = true
                    };
            var listenerTask = ListenAndRespondAsync("", cts.Token);
            c.Connect();
            Assert.That(() => c.IsConnected, Is.True.After(2000, 100), "Connect failed");
            cts.Cancel();
            cts = new CancellationTokenSource();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await listenerTask);
            Assert.That(() => c.IsConnected, Is.False.After(1000, 100), "Should be disconnected");
            listenerTask = ListenAndRespondAsync("", cts.Token);
            Assert.That(() => c.IsConnected, Is.True.After(5000, 100), "Reconnect failed");
            cts.Cancel();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await listenerTask);
            cts.Dispose();
            c.Dispose();
        }

        [Test]
        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        public async Task SendReceiveTest()
        {
            const string testMessage = "Hello world!";
            const string testResponse = "Hello back!";
            
            var listenerTask = ListenAndRespondAsync(testResponse);

            // Create connection and send data to listener
            var c = new TcpConnection(IPAddress.Loopback, TestPort);
            var messageReceivedByConnection = "";

            c.DataReceived += (_, args) =>
            {
                messageReceivedByConnection = Encoding.UTF8.GetString(args.Data.ToArray(), 0, args.Data.Count);
            };

            // Connect
            TestContext.WriteLine("Connecting");
            Assert.That(() => c.Connect(), Throws.Nothing.After(1000, 100));
            Assert.That(c.IsConnected, "Connection should report connected");

            // Send message and check responses
            TestContext.WriteLine("Sending message");
            c.Send(Encoding.UTF8.GetBytes(testMessage));

            var messageReceivedByListener = await listenerTask;

            Assert.That(() => messageReceivedByListener, Is.EqualTo(testMessage).After(1000, 100), "Listener did not receive message");
            Assert.That(() => messageReceivedByConnection, Is.EqualTo(testResponse).After(1000, 100), "Connection did not receive response");
            
            Assert.DoesNotThrow(() => c.Disconnect(), "Connection should disconnect");
            Assert.That(!c.IsConnected, "Connection should report disconnected");
            Assert.That(c.Client?.Client, Is.Null, "Socket should be null after its disconnected");
            Assert.DoesNotThrow(() => c.Dispose());
            c.Dispose();
        }


        private async Task<string> ListenAndRespondAsync(string response, CancellationToken ct = default)
        {
            TestContext.WriteLine("Starting Listener");
            var ipEndPoint = new IPEndPoint(IPAddress.Loopback, TestPort);
            using Socket listener = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(ipEndPoint);
            listener.Listen(100);

            // Wait for incoming connection
            TestContext.WriteLine("Wait for incoming connection");

            using var handler = await listener.AcceptAsync(ct);
            TestContext.WriteLine("Client connected");

            // Receive message
            var buffer = new byte[1024];
            TestContext.WriteLine("Waiting for message");
            int received = await handler.ReceiveAsync(buffer, SocketFlags.None, ct);
            var messageReceivedByListener = Encoding.UTF8.GetString(buffer, 0, received);
            TestContext.WriteLine("Message received: " + messageReceivedByListener);

            // Send response
            var echoBytes = Encoding.UTF8.GetBytes(response);
            await handler.SendAsync(echoBytes, 0, ct);

            return messageReceivedByListener;
        }
    }
}