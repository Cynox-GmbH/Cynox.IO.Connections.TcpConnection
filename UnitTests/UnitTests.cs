using System.Net;
using System.Net.Sockets;
using System.Text;
using Cynox.IO.Connections;

namespace UnitTests
{
    public class Tests
    {
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
        public async Task SendReceiveTest()
        {
            const string TEST_MESSAGE = "Hello world!";
            const string TEST_RESPONSE = "Hello back!";
            const int TEST_PORT = 11000;

            var localhostIp = IPAddress.Parse("127.0.0.1");
            string messageReceivedByListener = "";

            // Start listener
            var listenerTask = Task.Run(async () =>
            {
                var ipEndPoint = new IPEndPoint(localhostIp, TEST_PORT);
                using Socket listener = new(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(ipEndPoint);
                listener.Listen(100);

                // Wait for incoming connection
                TestContext.WriteLine("Wait for incoming connection");
                using var handler = await listener.AcceptAsync();
                
                // Receive message
                var buffer = new byte[1024];
                TestContext.WriteLine("Waiting for message");
                int received = await handler.ReceiveAsync(buffer, SocketFlags.None);
                messageReceivedByListener = Encoding.UTF8.GetString(buffer, 0, received);
                TestContext.WriteLine("Message received: " + messageReceivedByListener);

                // Send response
                byte[] echoBytes = Encoding.UTF8.GetBytes(TEST_RESPONSE);
                await handler.SendAsync(echoBytes, 0);
            });

            // Create connection and send data to listener
            var c = new TcpConnection(localhostIp, TEST_PORT);
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
            c.Send(Encoding.UTF8.GetBytes(TEST_MESSAGE));
            Assert.That(() => messageReceivedByListener, Is.EqualTo(TEST_MESSAGE).After(1000, 100), "Listener did not receive message");
            Assert.That(() => messageReceivedByConnection, Is.EqualTo(TEST_RESPONSE).After(1000, 100), "Connection did not receive response");
            
            Assert.DoesNotThrow(() => c.Disconnect(), "Connection should disconnect");
            Assert.That(!c.IsConnected, "Connection should report disconnected");
            Assert.That(c.Client?.Client, Is.Null, "Socket should be null after its disconnected");
            Assert.DoesNotThrow(() => c.Dispose());
            
            await listenerTask;
        }
    }
}