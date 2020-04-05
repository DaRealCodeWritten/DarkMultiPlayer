﻿using System;
using System.Security.Cryptography;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;
using DarkMultiPlayerCommon;
using DarkNetworkUDP;
using MessageStream2;

namespace DarkMultiPlayerServer.Messages
{
    public class Handshake
    {
        public static void SendHandshakeChallange(ClientObject client)
        {
            client.challange = new byte[1024];
            Random rand = new Random();
            rand.NextBytes(client.challange);
            NetworkMessage newMessage = NetworkMessage.Create((int)ServerMessageType.HANDSHAKE_CHALLANGE, 4 + client.challange.Length, NetworkMessageType.ORDERED_RELIABLE);
            using (MessageWriter mw = new MessageWriter(newMessage.data.data))
            {
                mw.Write<byte[]>(client.challange);
                newMessage.data.size = (int)mw.GetMessageLength();
            }
            ClientHandler.SendToClient(client, newMessage, true);
        }

        public static void HandleHandshakeResponse(ByteArray messageData, Connection<ClientObject> connection)
        {
            ClientObject client = connection.state;
            int protocolVersion;
            string playerName = "";
            string playerPublicKey;
            byte[] playerChallangeSignature;
            string clientVersion = "";
            string reason = "";
            Regex regex = new Regex(@"[\""<>|$]"); // Regex to detect quotation marks, and other illegal characters
            //0 - Success
            HandshakeReply handshakeReponse = HandshakeReply.HANDSHOOK_SUCCESSFULLY;
            try
            {
                using (MessageReader mr = new MessageReader(messageData.data))
                {
                    protocolVersion = mr.Read<int>();
                    playerName = mr.Read<string>();
                    playerPublicKey = mr.Read<string>();
                    playerChallangeSignature = mr.Read<byte[]>();
                    clientVersion = mr.Read<string>();
                }
            }
            catch (Exception e)
            {
                DarkLog.Debug("Error in HANDSHAKE_REQUEST from " + connection.state.playerName + ": " + e);
                SendHandshakeReply(connection.state, HandshakeReply.MALFORMED_HANDSHAKE, "Malformed handshake");
                return;
            }
            if (regex.IsMatch(playerName))
            {
                // Invalid username
                handshakeReponse = HandshakeReply.INVALID_PLAYERNAME;
                reason = "Invalid username";
            }
            if (playerName.Contains("/") || playerName.Contains(@"\") || playerName.Contains("\n") || playerName.Contains("\r"))
            {
                handshakeReponse = HandshakeReply.INVALID_PLAYERNAME;
                reason = "Invalid username";
            }
            if (protocolVersion != Common.PROTOCOL_VERSION)
            {
                //Protocol mismatch
                handshakeReponse = HandshakeReply.PROTOCOL_MISMATCH;
                reason = "Protocol mismatch";
            }
            if (handshakeReponse == HandshakeReply.HANDSHOOK_SUCCESSFULLY)
            {
                //Check client isn't already connected
                ClientObject testClient = ClientHandler.GetClientByName(playerName);
                if (ClientHandler.ClientConnected(testClient))
                {
                    handshakeReponse = HandshakeReply.ALREADY_CONNECTED;
                    reason = "Client already connected";
                }
            }
            if (handshakeReponse == HandshakeReply.HANDSHOOK_SUCCESSFULLY)
            {
                bool reserveKick = false;
                //Check the client isn't using a reserved name
                if (playerName == "Initial")
                {
                    reserveKick = true;
                }
                if (playerName == "Shared")
                {
                    reserveKick = true;
                }
                if (playerName == Settings.settingsStore.consoleIdentifier)
                {
                    reserveKick = true;
                }
                if (reserveKick)
                {
                    handshakeReponse = HandshakeReply.RESERVED_NAME;
                    reason = "Kicked for using a reserved name";
                }
            }
            if (handshakeReponse == HandshakeReply.HANDSHOOK_SUCCESSFULLY)
            {
                //Check the client matches any database entry
                string storedPlayerFile = Path.Combine(Server.universeDirectory, "Players", playerName + ".txt");
                string storedPlayerPublicKey = "";
                if (File.Exists(storedPlayerFile))
                {
                    storedPlayerPublicKey = File.ReadAllText(storedPlayerFile);
                    if (playerPublicKey != storedPlayerPublicKey)
                    {
                        handshakeReponse = HandshakeReply.INVALID_KEY;
                        reason = "Invalid key for user";
                    }
                    else
                    {
                        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(1024))
                        {
                            rsa.PersistKeyInCsp = false;
                            rsa.FromXmlString(playerPublicKey);
                            bool result = rsa.VerifyData(client.challange, CryptoConfig.CreateFromName("SHA256"), playerChallangeSignature);
                            if (!result)
                            {
                                handshakeReponse = HandshakeReply.INVALID_KEY;
                                reason = "Public/private key mismatch";
                            }
                        }
                    }
                }
                else
                {
                    try
                    {
                        File.WriteAllText(storedPlayerFile, playerPublicKey);
                        DarkLog.Debug("Client " + playerName + " registered!");
                    }
                    catch
                    {
                        handshakeReponse = HandshakeReply.INVALID_PLAYERNAME;
                        reason = "Invalid username";
                    }
                }
            }

            client.playerName = playerName;
            client.publicKey = playerPublicKey;
            client.clientVersion = clientVersion;

            if (handshakeReponse == HandshakeReply.HANDSHOOK_SUCCESSFULLY)
            {
                if (BanSystem.fetch.IsPlayerNameBanned(client.playerName) || BanSystem.fetch.IsIPBanned(client.ipAddress) || BanSystem.fetch.IsPublicKeyBanned(client.publicKey))
                {
                    handshakeReponse = HandshakeReply.PLAYER_BANNED;
                    reason = "You were banned from the server!";
                }
            }

            if (handshakeReponse == HandshakeReply.HANDSHOOK_SUCCESSFULLY)
            {
                if (ClientHandler.GetActiveClientCount() >= Settings.settingsStore.maxPlayers)
                {
                    handshakeReponse = HandshakeReply.SERVER_FULL;
                    reason = "Server is full";
                }
            }

            if (handshakeReponse == HandshakeReply.HANDSHOOK_SUCCESSFULLY)
            {
                if (Settings.settingsStore.whitelisted && !WhitelistSystem.fetch.IsWhitelisted(client.playerName))
                {
                    handshakeReponse = HandshakeReply.NOT_WHITELISTED;
                    reason = "You are not on the whitelist";
                }
            }

            if (handshakeReponse == HandshakeReply.HANDSHOOK_SUCCESSFULLY)
            {
                client.authenticated = true;
                string devClientVersion = "";
                DMPPluginHandler.FireOnClientAuthenticated(client);

                if (client.clientVersion.Length == 40)
                {
                    devClientVersion = client.clientVersion.Substring(0, 7);
                }
                else
                {
                    devClientVersion = client.clientVersion;
                }

                DarkLog.Normal("Client " + playerName + " handshook successfully, version: " + devClientVersion);
                SendHandshakeReply(client, handshakeReponse, "success");
                Server.playerCount = ClientHandler.GetActiveClientCount();
                Server.players = ClientHandler.GetActivePlayerNames();
                DarkLog.Debug("Online players is now: " + Server.playerCount + ", connected: " + ClientHandler.GetClients().Length);

            }
            else
            {
                DarkLog.Normal("Client " + playerName + " failed to handshake: " + reason);
                SendHandshakeReply(client, handshakeReponse, reason);
            }
        }

        private static void SendHandshakeReply(ClientObject client, HandshakeReply enumResponse, string reason)
        {
            NetworkMessage newMessage = NetworkMessage.Create((int)ServerMessageType.HANDSHAKE_REPLY, 512 * 1024, NetworkMessageType.ORDERED_RELIABLE);
            int response = (int)enumResponse;
            using (MessageWriter mw = new MessageWriter(newMessage.data.data))
            {
                mw.Write<int>(response);
                mw.Write<string>(reason);
                mw.Write<int>(Common.PROTOCOL_VERSION);
                mw.Write<string>(Common.PROGRAM_VERSION);
                if (response == 0)
                {
                    mw.Write<int>((int)Settings.settingsStore.modControl);
                    if (Settings.settingsStore.modControl != ModControlMode.DISABLED)
                    {
                        if (!File.Exists(Server.modFile))
                        {
                            Server.GenerateNewModFile();
                        }
                        string modFileData = File.ReadAllText(Server.modFile);
                        mw.Write<string>(modFileData);
                    }
                }
                newMessage.data.size = (int)mw.GetMessageLength();
            }
            ClientHandler.SendToClient(client, newMessage, true);
            if (response == 0 && Settings.settingsStore.modpackMode == ModpackMode.GAMEDATA)
            {
                Modpack.SendModList(client);
            }
            if (response == 0 && Settings.settingsStore.modpackMode == ModpackMode.CKAN)
            {
                Modpack.SendCkan(client);
            }
            Modpack.SendModDone(client);
        }
    }
}

