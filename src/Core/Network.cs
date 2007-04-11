using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Net;
using SS.Utilities;

namespace SS.Core
{
    public class Network
    {
        // TODO: think about how to send a reference to this class as well (in listenData?)
        public delegate void DataRecievedHandler(ListenData listenData, SubspaceBuffer buffer);
        public event DataRecievedHandler GameDataRecieved;
        public event DataRecievedHandler PingDataRecieved;

        private const int SELECT_TIMEOUT_MS = 1000;

        /// <summary>
        /// info about sockets this object has created, etc...
        /// </summary>
        private List<ListenData> knownListenData = new List<ListenData>();

        private BufferPool<SubspaceBuffer> _bufferPool;

        public Network(BufferPool<SubspaceBuffer> bufferPool)
        {
            _bufferPool = bufferPool;
        }

        // consider sending an equivalent ListenData like in ASSS
        public bool CreateSockets(int gamePort, IPAddress bindAddress)
        {
            int pingPort = gamePort + 1;

            try
            {
                Socket gameSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Socket pingSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

                gameSocket.Blocking = false;
                pingSocket.Blocking = false;

                if (bindAddress == null)
                    bindAddress = IPAddress.Any;

                gameSocket.Bind(new IPEndPoint(bindAddress, gamePort));
                pingSocket.Bind(new IPEndPoint(bindAddress, pingPort));

                knownListenData.Add(new ListenData(gameSocket, pingSocket));
                return true;
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }
        }

        public void ListenForPackets()
        {
            List<Socket> checkReadList = new List<Socket>(knownListenData.Count * 2);

            while (true)
            {
                try
                {
                    checkReadList.Clear();

                    foreach (ListenData ld in knownListenData)
                    {
                        checkReadList.Add(ld.GameSocket);
                        checkReadList.Add(ld.PingSocket);
                    }

                    Socket.Select(checkReadList, null, null, SELECT_TIMEOUT_MS * 1000);

                    foreach (Socket socket in checkReadList)
                    {
                        foreach (ListenData ld in knownListenData)
                        {
                            if (ld.GameSocket == socket)
                                handleGameDataRecieved(socket, ld);

                            if (ld.PingSocket == socket)
                                handlePingDataRecieved(socket, ld);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }

        private void handleGameDataRecieved(Socket s, ListenData ld)
        {
            SubspaceBuffer buffer = _bufferPool.GetBuffer();

            if (GameDataRecieved != null)
                GameDataRecieved(ld, buffer);
        }

        private void handlePingDataRecieved(Socket s, ListenData ld)
        {
            using (SubspaceBuffer buffer = _bufferPool.GetBuffer())
            {
                EndPoint recievedFrom = new IPEndPoint(IPAddress.Any, 0);
                int bytesRecieved = s.ReceiveFrom(buffer.Bytes, 4, SocketFlags.None, ref recievedFrom);
                IPEndPoint remoteEndPoint = (IPEndPoint)recievedFrom;

                if (bytesRecieved != 4)
                {
                    return;
                }

                // HACK: so that we can actually get something other than 0 ms :)
                Random random = new Random();
                int randomDelay = random.Next(100, 200);
                System.Threading.Thread.Sleep(randomDelay);

                // bytes from recieve
                buffer.Bytes[4] = buffer.Bytes[0];
                buffer.Bytes[5] = buffer.Bytes[1];
                buffer.Bytes[6] = buffer.Bytes[2];
                buffer.Bytes[7] = buffer.Bytes[3];

                // # players
                buffer.Bytes[0] = 1;
                buffer.Bytes[1] = 0;
                buffer.Bytes[2] = 0;
                buffer.Bytes[3] = 0;

                int bytesSent = s.SendTo(buffer.Bytes, 8, SocketFlags.None, remoteEndPoint);

                if (PingDataRecieved != null)
                    PingDataRecieved(ld, buffer);
            }
        }
    }
}
