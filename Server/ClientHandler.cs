using System;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using MessageStream;
using System.IO;
using DarkMultiPlayerCommon;

namespace DarkMultiPlayerServer
{
    public class ClientHandler
    {
        //No point support IPv6 until KSP enables it on their windows builds.
        private static TcpListener TCPServer;
        private static Queue<ClientObject> addClients;
        private static List<ClientObject> clients;
        private static Queue<ClientObject> deleteClients;
        #region Main loop
        public static void ThreadMain()
        {
            addClients = new Queue<ClientObject>();
            clients = new List<ClientObject>();
            deleteClients = new Queue<ClientObject>();
            SetupTCPServer();
            while (Server.serverRunning)
            {
                //Add new clients
                while (addClients.Count > 0)
                {
                    clients.Add(addClients.Dequeue());
                }
                //Process current clients
                foreach (ClientObject client in clients)
                {
                    CheckHeartBeat(client);
                    //HandleClientMessages(client);
                    SendOutgoingMessages(client);
                }
                //Delete old clients
                while (deleteClients.Count > 0)
                {
                    clients.Remove(deleteClients.Dequeue());
                }
                Thread.Sleep(10);
            }
            ShutdownTCPServer();
        }
        #endregion
        #region Server setup
        private static void SetupTCPServer()
        {
            try
            {
                TCPServer = new TcpListener(new IPEndPoint(IPAddress.Any, Settings.port));
                TCPServer.Start(4);
                TCPServer.BeginAcceptTcpClient(new AsyncCallback(NewClientCallback), null);
            }
            catch (Exception e)
            {
                DarkLog.Normal("Error setting up server, Exception: " + e);
                Server.serverRunning = false;
            }
            Server.serverStarting = false;
        }

        private static void ShutdownTCPServer()
        {
            TCPServer.Stop();
        }

        private static void NewClientCallback(IAsyncResult ar)
        {
            if (Server.serverRunning)
            {
                try
                {
                    TcpClient newClient = TCPServer.EndAcceptTcpClient(ar);
                    SetupClient(newClient);
                    DarkLog.Normal("New client connection from " + newClient.Client.RemoteEndPoint);
                }
                catch
                {
                    DarkLog.Normal("Error accepting client!");
                }
                TCPServer.BeginAcceptTcpClient(new AsyncCallback(NewClientCallback), null);
            }
        }

        private static void SetupClient(TcpClient newClientConnection)
        {
            ClientObject newClientObject = new ClientObject();
            newClientObject.playerStatus = new PlayerStatus();
            newClientObject.connectionStatus = ConnectionStatus.CONNECTED;
            newClientObject.playerName = "Unknown";
            newClientObject.activeVessel = "";
            newClientObject.endpoint = newClientConnection.Client.RemoteEndPoint.ToString();
            //Keep the connection reference
            newClientObject.connection = newClientConnection;
            //Add the queues
            newClientObject.sendMessageQueueHigh = new Queue<ServerMessage>();
            newClientObject.sendMessageQueueSplit = new Queue<ServerMessage>();
            newClientObject.sendMessageQueueLow = new Queue<ServerMessage>();
            newClientObject.receiveMessageQueue = new Queue<ClientMessage>();
            StartReceivingIncomingMessages(newClientObject);
            addClients.Enqueue(newClientObject);
        }
        #endregion
        #region Network related methods
        private static void CheckHeartBeat(ClientObject client)
        {
            if (client.sendMessageQueueHigh.Count == 0 && client.sendMessageQueueSplit.Count == 0 && client.sendMessageQueueLow.Count == 0)
            {
                long currentTime = Server.serverClock.ElapsedMilliseconds;
                if ((currentTime - client.lastSendTime) > Common.HEART_BEAT_INTERVAL)
                {
                    SendHeartBeat(client);
                }
            }
        }

        private static void SendOutgoingMessages(ClientObject client)
        {
            if (!client.isSendingToClient)
            {
                if (client.sendMessageQueueHigh.Count > 0)
                {
                    ServerMessage message = client.sendMessageQueueHigh.Dequeue();
                    SendNetworkMessage(client, message);
                    return;
                }
                if (client.sendMessageQueueSplit.Count > 0)
                {
                    ServerMessage message = client.sendMessageQueueSplit.Dequeue();
                    SendNetworkMessage(client, message);
                    return;
                }
                if (client.sendMessageQueueLow.Count > 0)
                {
                    ServerMessage message = client.sendMessageQueueLow.Dequeue();
                    SendNetworkMessage(client, message);
                    return;
                }
            }
        }

        private static void SendNetworkMessage(ClientObject client, ServerMessage message)
        {
            //Write the send times down in SYNC_TIME_REPLY packets
            if (message.type == ServerMessageType.SYNC_TIME_REPLY)
            {
                try
                {
                    using (MessageWriter mw = new MessageWriter(0, false))
                    {
                        using (MessageReader mr = new MessageReader(message.data, false))
                        {
                            //Client send time
                            mw.Write<long>(mr.Read<long>());
                            //Server receive time
                            mw.Write<long>(mr.Read<long>());
                            //Server send time
                            mw.Write<long>(DateTime.UtcNow.Ticks);
                            message.data = mw.GetMessageBytes();
                        }
                    }
                }
                catch (Exception e)
                {
                    DarkLog.Debug("Error rewriting SYNC_TIME packet, Exception " + e);
                }
            }
            //Continue sending
            byte[] messageBytes;
            using (MessageWriter mw = new MessageWriter((int)message.type, true))
            {
                if (message.data != null)
                {
                    mw.Write<byte[]>(message.data);
                }
                messageBytes = mw.GetMessageBytes();
            }
            client.isSendingToClient = true;
            client.lastSendTime = Server.serverClock.ElapsedMilliseconds;
            if (client.connectionStatus == ConnectionStatus.CONNECTED)
            {
                try
                {
                    client.connection.GetStream().BeginWrite(messageBytes, 0, messageBytes.Length, new AsyncCallback(SendMessageCallback), client);
                }
                catch (Exception e)
                {
                    DarkLog.Normal("Client " + client.playerName + " disconnected, endpoint " + client.endpoint + " error: " + e.Message);
                    DisconnectClient(client);
                }
            }
            if (message.type == ServerMessageType.CONNECTION_END)
            {
                DarkLog.Normal("Client " + client.playerName + " disconnected, sent CONNECTION_END to endpoint " + client.endpoint);
                DisconnectClient(client);
            }
        }

        private static void SendMessageCallback(IAsyncResult ar)
        {
            ClientObject client = (ClientObject)ar.AsyncState;
            try
            {
                client.connection.GetStream().EndWrite(ar);
            }
            catch (Exception e)
            {
                DarkLog.Normal("Client " + client.playerName + " disconnected, endpoint " + client.endpoint + ", error: " + e.Message);
                DisconnectClient(client);
            }
            client.isSendingToClient = false;
        }

        private static void StartReceivingIncomingMessages(ClientObject client)
        {
            client.lastReceiveTime = Server.serverClock.ElapsedMilliseconds;
            //Allocate byte for header
            client.receiveMessage = new ClientMessage();
            client.receiveMessage.data = new byte[8];
            client.receiveMessageBytesLeft = client.receiveMessage.data.Length;
            try
            {
                client.connection.GetStream().BeginRead(client.receiveMessage.data, client.receiveMessage.data.Length - client.receiveMessageBytesLeft, client.receiveMessageBytesLeft, new AsyncCallback(ReceiveCallback), client);
            }
            catch (Exception e)
            {
                DarkLog.Normal("Connection error: " + e.Message);
                DisconnectClient(client);
            }
        }

        private static void ReceiveCallback(IAsyncResult ar)
        {
            ClientObject client = (ClientObject)ar.AsyncState;
            try
            {
                client.receiveMessageBytesLeft -= client.connection.GetStream().EndRead(ar);
                if (client.receiveMessageBytesLeft == 0)
                {
                    //We either have the header or the message data, let's do something
                    if (!client.isReceivingMessage)
                    {
                        //We have the header
                        using (MessageReader mr = new MessageReader(client.receiveMessage.data, true))
                        {
                            if (mr.GetMessageType() > (Enum.GetNames(typeof(ClientMessageType)).Length - 1))
                            {
                                //Malformed message, most likely from a non DMP-client.
                                SendConnectionEnd(client, "Invalid DMP message. Disconnected.");
                                DarkLog.Normal("Invalid DMP message from " + client.endpoint);
                                //Returning from ReceiveCallback will break the receive loop and stop processing any further messages.
                                return;
                            }
                            client.receiveMessage.type = (ClientMessageType)mr.GetMessageType();
                            int length = mr.GetMessageLength();
                            if (length == 0)
                            {
                                //Null message, handle it.
                                client.receiveMessage.data = null;
                                HandleMessage(client, client.receiveMessage);
                                client.receiveMessage.type = 0;
                                client.receiveMessage.data = new byte[8];
                                client.receiveMessageBytesLeft = client.receiveMessage.data.Length;
                            }
                            else
                            {
                                if (length < Common.MAX_MESSAGE_SIZE)
                                {
                                    client.isReceivingMessage = true;
                                    client.receiveMessage.data = new byte[length];
                                    client.receiveMessageBytesLeft = client.receiveMessage.data.Length;
                                }
                                else
                                {
                                    //Malformed message, most likely from a non DMP-client.
                                    SendConnectionEnd(client, "Invalid DMP message. Disconnected.");
                                    DarkLog.Normal("Invalid DMP message from " + client.endpoint);
                                    //Returning from ReceiveCallback will break the receive loop and stop processing any further messages.
                                    return;
                                }
                            }
                        }
                    }
                    else
                    {
                        //We have the message data to a non-null message, handle it
                        client.isReceivingMessage = false;
                        using (MessageReader mr = new MessageReader(client.receiveMessage.data, false))
                        {
                            client.receiveMessage.data = mr.Read<byte[]>();
                        }
                        HandleMessage(client, client.receiveMessage);
                        client.receiveMessage.type = 0;
                        client.receiveMessage.data = new byte[8];
                        client.receiveMessageBytesLeft = client.receiveMessage.data.Length;
                    }
                }
                if (client.connectionStatus == ConnectionStatus.CONNECTED)
                {
                    client.lastReceiveTime = Server.serverClock.ElapsedMilliseconds;
                    client.connection.GetStream().BeginRead(client.receiveMessage.data, client.receiveMessage.data.Length - client.receiveMessageBytesLeft, client.receiveMessageBytesLeft, new AsyncCallback(ReceiveCallback), client);
                }
            }
            catch (Exception e)
            {
                DarkLog.Normal("Connection error: " + e.Message);
                DisconnectClient(client);
            }
        }

        private static void DisconnectClient(ClientObject client)
        {
            if (client.connectionStatus != ConnectionStatus.DISCONNECTED)
            {
                client.connectionStatus = ConnectionStatus.DISCONNECTED;
                ServerMessage newMessage = new ServerMessage();
                newMessage.type = ServerMessageType.PLAYER_DISCONNECT;
                using (MessageWriter mw = new MessageWriter(0, false))
                {
                    mw.Write<string>(client.playerName);
                    newMessage.data = mw.GetMessageBytes();
                }
                SendToAll(client, newMessage, true);
                deleteClients.Enqueue(client);
                if (client.connection != null)
                {
                    client.connection.Close();
                }
            }
        }
        #endregion
        #region Message handling
        private static void HandleMessage(ClientObject client, ClientMessage message)
        {
            //DarkLog.Debug("Got " + message.type + " from " + client.playerName);

            //Clients can only send HEARTBEATS, HANDSHAKE_REQUEST or CONNECTION_END's until they are authenticated.
            if (!client.authenticated && !(message.type == ClientMessageType.HEARTBEAT || message.type == ClientMessageType.HANDSHAKE_REQUEST || message.type == ClientMessageType.CONNECTION_END))
            {
                SendConnectionEnd(client, "You must authenticate before attempting to send a " + message.type.ToString() + " message");
                return;
            }

            switch (message.type)
            {
                case ClientMessageType.HEARTBEAT:
                    //Don't do anything for heartbeats, they just keep the connection alive
                    break;
                case ClientMessageType.HANDSHAKE_REQUEST:
                    HandleHandshakeRequest(client, message.data);
                    break;
                case ClientMessageType.PLAYER_STATUS:
                    HandlePlayerStatus(client, message.data);
                    break;
                case ClientMessageType.SYNC_TIME_REQUEST:
                    HandleSyncTimeRequest(client, message.data);
                    break;
                case ClientMessageType.KERBALS_REQUEST:
                    HandleKerbalsRequest(client);
                    break;
                case ClientMessageType.KERBAL_PROTO:
                    HandleKerbalProto(client, message.data);
                    break;
                case ClientMessageType.VESSELS_REQUEST:
                    HandleVesselsRequest(client);
                    break;
                case ClientMessageType.VESSEL_PROTO:
                    HandleVesselProto(client, message.data);
                    break;
                case ClientMessageType.VESSEL_UPDATE:
                    HandleVesselUpdate(client, message.data);
                    break;
                case ClientMessageType.SEND_ACTIVE_VESSEL:
                    HandleSendActiveVessel(client, message.data);
                    break;
                case ClientMessageType.TIME_LOCK_REQUEST:
                    HandleTimeLockRequest(client);
                    break;
                case ClientMessageType.CONNECTION_END:
                    HandleConnectionEnd(client, message.data);
                    break;
                default:
                    DarkLog.Debug("Unhandled message type " + message.type);
                    break;
            }
        }

        private static void HandleHandshakeRequest(ClientObject client, byte[] messageData)
        {
            try
            {
                int protocolVersion;
                string playerName = "";
                string playerGuid = Guid.Empty.ToString();
                string reason = "";
                //0 - Success
                int handshakeReponse = 0;
                using (MessageReader mr = new MessageReader(messageData, false))
                {
                    protocolVersion = mr.Read<int>();
                    playerName = mr.Read<string>();
                    playerGuid = mr.Read<string>();
                }
                if (protocolVersion != Common.PROTOCOL_VERSION)
                {
                    //Protocol mismatch
                    handshakeReponse = 1;
                    reason = "Protocol mismatch";
                }
                if (handshakeReponse == 0)
                {
                    //Check client isn't already connected
                    foreach (ClientObject testClient in clients)
                    {
                        if (client != testClient && testClient.playerName == playerName)
                        {
                            handshakeReponse = 2;
                            reason = "Client already connected";
                        }
                    }
                }
                if (handshakeReponse == 0)
                {
                    //Check the client matches any database entry
                    string storedPlayerFile = Path.Combine(Server.universeDirectory, "Players", playerName + ".txt");
                    string storedPlayerGuid = "";
                    if (File.Exists(storedPlayerFile))
                    {
                        using (StreamReader sr = new StreamReader(storedPlayerFile))
                        {
                            storedPlayerGuid = sr.ReadLine();
                        }
                        if (playerGuid != storedPlayerGuid)
                        {
                            handshakeReponse = 3;
                            reason = "Invalid player token for user";
                        }
                    }
                    else
                    {
                        DarkLog.Debug("Client " + client.playerName + " registered!");
                        using (StreamWriter sw = new StreamWriter(storedPlayerFile))
                        {
                            sw.WriteLine(playerGuid);
                        }
                    }
                }
                client.playerName = playerName;
                if (handshakeReponse == 0)
                {
                    client.authenticated = true;
                    DarkLog.Normal("Client " + playerName + " handshook successfully!");
                    SendHandshakeReply(client, handshakeReponse);
                    SendServerSettings(client);
                    SendAllActiveVessels(client);
                    SendAllPlayerStatus(client);
                }
                else
                {
                    DarkLog.Normal("Client " + playerName + " failed to handshake, reason " + reason);
                    SendHandshakeReply(client, handshakeReponse);
                    SendConnectionEnd(client, reason);
                }


            }
            catch (Exception e)
            {
                DarkLog.Debug("Error in HANDSHAKE_REQUEST from " + client.playerName + ": " + e);
                SendHandshakeReply(client, 99);
                SendConnectionEnd(client, "Malformed handshake");
            }
        }

        private static void HandleSyncTimeRequest(ClientObject client, byte[] messageData)
        {
            try
            {
                ServerMessage newMessage = new ServerMessage();
                newMessage.type = ServerMessageType.SYNC_TIME_REPLY;
                using (MessageWriter mw = new MessageWriter(0, false))
                {
                    using (MessageReader mr = new MessageReader(messageData, false))
                    {
                        //Client send time
                        mw.Write<long>(mr.Read<long>());
                        //Server receive time
                        mw.Write<long>(DateTime.UtcNow.Ticks);
                        newMessage.data = mw.GetMessageBytes();
                    }
                }
                SendToClient(client, newMessage, true);

            }
            catch (Exception e)
            {
                DarkLog.Debug("Error in SYNC_TIME_REQUEST from " + client.playerName + ": " + e);
                DisconnectClient(client);
            }
        }

        private static void HandlePlayerStatus(ClientObject client, byte[] messageData)
        {
            using (MessageReader mr = new MessageReader(messageData, false))
            {
                string playerName = mr.Read<string>();
                if (playerName != client.playerName)
                {
                    DarkLog.Debug(client.playerName + " tried to send an update for " + playerName + ", kicking.");
                    SendConnectionEnd(client, "Kicked for sending an update for another player");
                    return;
                }
                client.playerStatus.vesselText = mr.Read<string>();
                client.playerStatus.statusText = mr.Read<string>();
            }
            //Relay the message
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.PLAYER_STATUS;
            newMessage.data = messageData;
            SendToAll(client, newMessage, false);
        }

        private static void HandleKerbalsRequest(ClientObject client)
        {
            DarkLog.Debug("Sending " + client.playerName + " kerbals...");
            //Send vessels here
            foreach (string file in Directory.GetFiles(Server.universeDirectory, "Kerbals"))
            {
                using (StreamReader sr = new StreamReader(file))
                {
                    string kerbalData = sr.ReadToEnd();
                    SendKerbal(client, kerbalData);
                }
            }
            SendKerbalsComplete(client);
        }

        private static void HandleKerbalProto(ClientObject client, byte[] messageData)
        {
            //Send kerbal
            using (MessageReader mr = new MessageReader(messageData, false))
            {
                string kerbalName = mr.Read<string>();
                string kerbalData = mr.Read<string>();
                using (StreamWriter sw = new StreamWriter(Path.Combine(Server.universeDirectory, "Kerbals", kerbalName + ".txt")))
                {
                    sw.Write(kerbalData);
                    ServerMessage newMessage = new ServerMessage();
                    newMessage.type = ServerMessageType.KERBAL_REPLY;
                    using (MessageWriter mw = new MessageWriter(0, false))
                    {
                        mw.Write<string>(kerbalData);
                        newMessage.data = mw.GetMessageBytes();
                    }
                    SendToAll(client, newMessage, false);
                }
            }
        }

        private static void HandleVesselsRequest(ClientObject client)
        {
            DarkLog.Debug("Sending " + client.playerName + " vessels...");
            //Send vessels here
            foreach (string file in Directory.GetFiles(Path.Combine(Server.universeDirectory, "Vessels")))
            {
                using (StreamReader sr = new StreamReader(file))
                {
                    string vesselData = sr.ReadToEnd();
                    SendVessel(client, vesselData);
                }
            }
            SendVesselsComplete(client);
        }

        private static void HandleVesselProto(ClientObject client, byte[] messageData)
        {
            //Send kerbal
            using (MessageReader mr = new MessageReader(messageData, false))
            {
                string vesselGuid = mr.Read<string>();
                string vesselData = mr.Read<string>();
                using (StreamWriter sw = new StreamWriter(Path.Combine(Server.universeDirectory, "Vessels", vesselGuid + ".txt")))
                {
                    sw.Write(vesselData);
                    ServerMessage newMessage = new ServerMessage();
                    newMessage.type = ServerMessageType.VESSEL_PROTO;
                    using (MessageWriter mw = new MessageWriter(0, false))
                    {
                        mw.Write<string>(vesselData);
                        newMessage.data = mw.GetMessageBytes();
                    }
                    SendToAll(client, newMessage, false);
                }
            }
        }

        private static void HandleVesselUpdate(ClientObject client, byte[] messageData)
        {
            //We only relay this message.
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.VESSEL_UPDATE;
            newMessage.data = messageData;
            SendToAll(client, newMessage, false);
        }

        private static void HandleSendActiveVessel(ClientObject client, byte[] messageData)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.SET_ACTIVE_VESSEL;
            using (MessageReader mr = new MessageReader(messageData, false))
            {
                //We don't care about the player name, just need to advance message reader past it.
                mr.Read<string>();
                string activeVessel = mr.Read<string>();
                client.activeVessel = activeVessel;
            }
            newMessage.data = messageData;
            SendToAll(client, newMessage, true);
        }

        private static void HandleTimeLockRequest(ClientObject client)
        {
            DarkLog.Debug("Sending " + client.playerName + " time lock...");
            SendTimeLockReply(client);
        }

        private static void HandleConnectionEnd(ClientObject client, byte[] messageData)
        {
            string reason = "Unknown";
            try
            {
                using (MessageReader mr = new MessageReader(messageData, false))
                {
                    reason = mr.Read<string>();
                }
            }
            catch (Exception e)
            {
                DarkLog.Debug("Error handling CONNECTION_END message from " + client.playerName + ":" + e);
            }
            DarkLog.Debug(client.playerName + " sent connection end message, reason: " + reason);
            DisconnectClient(client);
        }
        #endregion
        #region Message sending
        //Call with null client to send to all clients
        private static void SendToAll(ClientObject ourClient, ServerMessage message, bool highPriority)
        {
            foreach (ClientObject otherClient in clients)
            {
                if (ourClient != otherClient)
                {
                    SendToClient(otherClient, message, highPriority);
                }
            }
        }

        private static void SendToClient(ClientObject client, ServerMessage message, bool highPriority)
        {
            if (highPriority)
            {
                client.sendMessageQueueHigh.Enqueue(message);
            }
            else
            {
                client.sendMessageQueueLow.Enqueue(message);
            }
        }

        private static void SendHeartBeat(ClientObject client)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.HEARTBEAT;
            SendToClient(client, newMessage, true);
        }

        private static void SendHandshakeReply(ClientObject client, int response)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.HANDSHAKE_REPLY;
            using (MessageWriter mw = new MessageWriter(0, false))
            {
                mw.Write<int>(response);
                newMessage.data = mw.GetMessageBytes();
            }
            SendToClient(client, newMessage, true);
        }

        private static void SendServerSettings(ClientObject client)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.SERVER_SETTINGS;
            using (MessageWriter mw = new MessageWriter(0, false))
            {
                mw.Write<int>((int)Settings.warpMode);
                newMessage.data = mw.GetMessageBytes();
            }
            SendToClient(client, newMessage, true);
        }

        private static void SendAllPlayerStatus(ClientObject client)
        {
            foreach (ClientObject otherClient in clients)
            {
                if (otherClient.authenticated)
                {
                    if (otherClient != client)
                    {
                        ServerMessage newMessage = new ServerMessage();
                        newMessage.type = ServerMessageType.PLAYER_STATUS;
                        using (MessageWriter mw = new MessageWriter(0, false))
                        {
                            mw.Write<string>(otherClient.playerName);
                            mw.Write<string>(otherClient.playerStatus.vesselText);
                            mw.Write<string>(otherClient.playerStatus.statusText);
                            newMessage.data = mw.GetMessageBytes();
                        }
                        SendToClient(client, newMessage, false);
                    }
                }
            }
        }

        private static void SendKerbalsComplete(ClientObject client)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.KERBAL_COMPLETE;
            SendToClient(client, newMessage, false);
        }

        private static void SendAllActiveVessels(ClientObject client)
        {
            foreach (ClientObject otherClient in clients)
            {
                if (otherClient.authenticated && otherClient.activeVessel != "")
                {
                    if (otherClient != client)
                    {
                        ServerMessage newMessage = new ServerMessage();
                        newMessage.type = ServerMessageType.SET_ACTIVE_VESSEL;
                        using (MessageWriter mw = new MessageWriter(0, false))
                        {
                            mw.Write<string>(otherClient.playerName);
                            mw.Write<string>(otherClient.activeVessel);
                            newMessage.data = mw.GetMessageBytes();
                        }
                        SendToClient(client, newMessage, false);
                    }
                }
            }
        }

        private static void SendKerbal(ClientObject client, string kerbalData)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.KERBAL_REPLY;
            using (MessageWriter mw = new MessageWriter(0, false))
            {
                mw.Write<string>(kerbalData);
                newMessage.data = mw.GetMessageBytes();
            }
            SendToClient(client, newMessage, false);
        }

        private static void SendVessel(ClientObject client, string vesselData)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.VESSEL_PROTO;
            using (MessageWriter mw = new MessageWriter(0, false))
            {
                mw.Write<string>(vesselData);
                newMessage.data = mw.GetMessageBytes();
            }
            SendToClient(client, newMessage, false);
        }

        private static void SendVesselsComplete(ClientObject client)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.VESSEL_COMPLETE;
            SendToClient(client, newMessage, false);
        }

        private static void SendTimeLockReply(ClientObject client)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.TIME_LOCK_REPLY;
            using (MessageWriter mw = new MessageWriter(0, false))
            {
                mw.Write<long>(DateTime.UtcNow.Ticks);
                mw.Write<double>(100d);
                mw.Write<float>(1f);
                newMessage.data = mw.GetMessageBytes();
            }
            SendToClient(client, newMessage, true);
        }

        private static void SendConnectionEnd(ClientObject client, string reason)
        {
            ServerMessage newMessage = new ServerMessage();
            newMessage.type = ServerMessageType.CONNECTION_END;
            using (MessageWriter mw = new MessageWriter(0, false))
            {
                mw.Write<string>(reason);
                newMessage.data = mw.GetMessageBytes();
            }
            SendToClient(client, newMessage, true);
        }
        #endregion
    }

    public class ClientObject
    {
        public bool authenticated;
        public string playerName;
        public string activeVessel;
        public string endpoint;
        public TcpClient connection;
        public long lastSendTime;
        public bool isSendingToClient;
        public Queue<ServerMessage> sendMessageQueueHigh;
        public Queue<ServerMessage> sendMessageQueueSplit;
        public Queue<ServerMessage> sendMessageQueueLow;
        public Queue<ClientMessage> receiveMessageQueue;
        public long lastReceiveTime;
        public bool isReceivingMessage;
        public int receiveMessageBytesLeft;
        public ClientMessage receiveMessage;
        public ConnectionStatus connectionStatus;
        public PlayerStatus playerStatus;
    }
}

