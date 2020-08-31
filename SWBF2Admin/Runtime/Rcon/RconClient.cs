﻿/*
 * This file is part of SWBF2Admin (https://github.com/jweigelt/swbf2admin). 
 * Copyright(C) 2017, 2018  Jan Weigelt <jan@lekeks.de>
 *
 * SWBF2Admin is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.

 * SWBF2Admin is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
 * GNU General Public License for more details.

 * You should have received a copy of the GNU General Public License
 * along with SWBF2Admin. If not, see<http://www.gnu.org/licenses/>.
 */
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using SWBF2Admin.Config;
using SWBF2Admin.Utility;
using SWBF2Admin.Structures;
using SWBF2Admin.Runtime.Rcon.Packets;
using System.Collections.Generic;

namespace SWBF2Admin.Runtime.Rcon
{
    /// <summary>
    /// class for connecting to the remote console ("rcon") server
    /// </summary>
    public class RconClient : ComponentBase
    {

        private const byte LOGIN_MAGIC = 0x64;
        private const byte SLEEP = 5;
        private const string STATUS_MESSAGE_GAME_HAS_ENDED = "Game has ended";
        private const string STATUS_MESSAGE_SERVER_IS_BUSY = "busy";
        private const int CHAR_LIMIT = 120; // Really its 128

        private readonly object rxLock = new object();

        public RconClient(AdminCore core) : base(core) { }

        public override void OnServerStart(EventArgs e)
        {
            ServerPassword = Core.Server.Settings.AdminPw;
            ServerIPEP = new IPEndPoint(IPAddress.Parse(Core.Server.Settings.IP), Core.Server.Settings.RconPort);
            Start();
        }

        public override void OnServerStop()
        {
            Stop();
        }

        public override void OnDeInit()
        {
            Stop();
        }

        /// <summary>
        /// Called when the rcon-connection is lost
        /// </summary>
        public event EventHandler Disconnected;

        /// <summary>
        /// Called when new chat is received
        /// </summary>
        public event EventHandler ChatInput;

        /// <summary>
        /// Called when chat is sent
        /// </summary>
        public event EventHandler ChatOutput;

        /// <summary>
        /// Called when a match ended
        /// </summary>
        public event EventHandler GameEnded;

        /// <summary>
        /// Rcon-servers IPEndPoint
        /// </summary>
        public IPEndPoint ServerIPEP { get; set; }

        /// <summary>
        /// server's admin password
        /// </summary>
        public string ServerPassword { get; set; }

        /// <summary>
        /// max. time (in ms) before a packet is dropped if the server doesn't respond
        /// </summary>
        public int PacketTimeout { get; set; } = 500;

        private bool running = false;

        private Thread workThread;

        private TcpClient client;
        private BinaryReader reader;
        private BinaryWriter writer;

        private string lastMessage = null;

        /// <summary>
        /// Connects to rcon-server and authenticates
        /// </summary>
        private void Start()
        {
            if (running) return;

            client = new TcpClient();

            try
            {
                client.Connect(ServerIPEP);
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "Failed to connect to rcon server at '{0}' ({1})", ServerIPEP.ToString(), e.ToString());
                throw new RconException(e);
            }

            reader = new BinaryReader(client.GetStream());
            writer = new BinaryWriter(client.GetStream());
            Logger.Log(LogLevel.Info, "Connected to rcon server at '{0}'. Sending login...", ServerIPEP.ToString());

            try
            {
                Login();
                Logger.Log(LogLevel.Info, "Login OK. Rcon ready.");
            }
            catch (RconNotAuthorizedException e)
            {
                Logger.Log(LogLevel.Error, "Server refused login.");
                throw new RconException((Exception)e);
            }
            catch (Exception e)
            {
                Logger.Log(LogLevel.Error, "Failed to send Login ({0})", e.ToString());
                throw new RconException(e);
            }

            running = true;
            workThread = new Thread(WorkThread_Run);
            workThread.Start();
        }

        /// <summary>
        /// Closes the current connection
        /// </summary>
        private void Stop()
        {
            running = false;
            if (client != null) client.Dispose();
            if (workThread != null) workThread.Join();
        }

        #region TX
        /// <summary>
        /// Filters command string to prevent injection vulns
        /// </summary>
        /// <param name="cmd"></param>
        /// <returns></returns>
        public string FilterString(string cmd)
        {
            //Prevent command injection
            return cmd.Replace('/', '\\');
        }

        /// <summary>
        /// Sends a packet and retrieves the response
        /// </summary>
        /// <param name="packet"></param>
        public void SendPacket(RconPacket packet)
        {
            string lastMessageTemp;
            lock (rxLock)
            {
                lastMessage = null;
            }

            try
            {
                Logger.Log(LogLevel.Verbose, "Sending command: '{0}'", packet.Command);
                Send(packet.Command);
            }
            catch (Exception e)
            {
                Logger.Log(running ? LogLevel.Warning : LogLevel.Verbose, "SendPacket failed. ({0})", e.ToString());
                return;
            }

            DateTime start = DateTime.Now;
            do
            {
                if ((DateTime.Now - start).TotalMilliseconds < PacketTimeout)
                {
                    Thread.Sleep(SLEEP);
                }
                else
                {
                    Logger.Log(LogLevel.Warning, "Rcon packet timeout. (running {0})", packet.Command);
                    return;
                }

                lock (rxLock)
                {
                    lastMessageTemp = lastMessage;
                }
            } while (lastMessageTemp == null);

            if (lastMessageTemp == STATUS_MESSAGE_SERVER_IS_BUSY)
            {
                Logger.Log(LogLevel.Verbose, "Server is busy - dropping rcon packet");
            }
            else
            {
                packet.HandleResponse(lastMessageTemp);
            }

            lock (rxLock)
            {
                lastMessage = null;
            }
        }

        /// <summary>
        /// Broadcasts a message in chat
        /// </summary>
        /// <param name="message"></param>
        public void Say(string message)
        {
            ChatOutput.Invoke(this, new RconChatEventArgs(new ChatMessage(message)));
            foreach (var segment in Util.SegmentString(message, CHAR_LIMIT))
            {
                SendCommand("say", segment);
            }
        }

        /// <summary>
        /// Sends a privte message to a player
        /// <note>Use with caution as sending too many PMs will cause the chat to get slow/stuck.</note>
        /// </summary>
        /// <param name="message"></param>
        /// <param name="player"></param>
        public void Pm(string message, Player player)
        {
            ChatOutput.Invoke(this, new RconChatEventArgs(new ChatMessage($"[=> {player.Name}] {message}")));
            foreach (var segment in Util.SegmentString(message, CHAR_LIMIT))
            {
                SendCommand("warn", player.Slot.ToString(), segment);
            }
        }

        /// <summary>
        /// Runs lua code in the server's ingame scripting context
        /// </summary>
        /// <param name="lua">lua code to run</param>
        public void IngameLua(string lua)
        {
            SendCommand("lua", lua);
        }

        public bool UpdateServerSettings(List<ServerSettings.ServerSetting> settings)
        {
            Logger.Log(LogLevel.Verbose, "Updating {0} setting(s)", settings.Count.ToString());
            foreach (var setting in settings)
            {
                SendPacket(new UpdateSettingPacket(setting));
            }
            return false;
        }

        public void UpdateMapList(List<string> newMapList)
        {
            var mp = new MapListPacket();
            SendPacket(mp);
            if (!mp.PacketOk)
            {
                Logger.Log(LogLevel.Error, "Map update failed - invalid /maps response");
                return;
            }

            int added = 0;
            int dropped = 0;
            foreach (var s in mp.MapList)
            {
                if (!newMapList.Contains(s))
                {
                    SendCommand("dropmap", s);
                    dropped++;
                }
            }

            foreach (var s in newMapList)
            {
                if (!mp.MapList.Contains(s))
                {
                    SendCommand("addmap", s);
                    added++;
                }
            }
            Logger.Log(LogLevel.Verbose, "Added {0} maps, dropped {1} maps", added.ToString(), dropped.ToString());
        }

        /// <summary>
        /// Sends a command to the server but does not retrieve the response
        /// <note>
        /// Only use this method if your command doesn't output anything,
        /// otherwise the receive thread will lock until PacketTimeout expired. 
        /// </note>
        /// </summary>
        /// <param name="command"></param>
        /// <param name="p"></param>
        public void SendCommandNoResponse(string command, params string[] p)
        {
            Send(command + " " + string.Join(" ", p));
        }

        /// <summary>
        /// Sends a command to the server and retrieves the response
        /// </summary>
        /// <param name="command"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        public string SendCommand(string command, params string[] p)
        {
            RconPacket packet = new RconPacket(command + " " + string.Join(" ", p));
            SendPacket(packet);
            return packet.Response;
        }

        /// <summary>
        /// Sends authentication to server
        /// </summary>
        private void Login()
        {
            byte[] buf = Util.StrToBytes(Util.Md5(ServerPassword));
            writer.Write(buf);
            writer.Write(LOGIN_MAGIC);
            writer.Flush();
            if (reader.ReadByte() != 0x01) throw new RconNotAuthorizedException();
        }

        /// <summary>
        /// Sends a raw string to the server
        /// </summary>
        private void Send(string command)
        {
            //filter any /'s to prevent command injection
            byte[] buffer = Util.StrToBytes(FilterString(command));

            writer.Write((byte)1);
            writer.Write((byte)(buffer.Length + 2));
            writer.Write('/');
            writer.Write(buffer);
            writer.Write((byte)0);
            writer.Flush();
        }
        #endregion

        #region RX
        /// <summary>
        /// Thread handling inbound data
        /// </summary>
        private void WorkThread_Run()
        {
            string message = string.Empty;
            byte[] buffer = new byte[byte.MaxValue];
            byte rows = 0, rowLen = 0, bytesRead = 0;
            bool eos = false;
            try
            {
                while (running && !eos)
                {
                    rows = reader.ReadByte();
                    for (byte i = 0; i < rows; i++)
                    {
                        rowLen = reader.ReadByte();
                        while (bytesRead < rowLen) bytesRead += (byte)reader.Read(buffer, bytesRead, (rowLen - bytesRead));

                        message += Util.BytesToStr(buffer, rowLen);
                        message = message.Substring(0, message.Length - 1);

                        if (i + 1 < rows) message = message + "\n";

                        bytesRead = 0;
                    }
                    // Logger.Log(LogLevel.Verbose, "Read rcon message: {0} bytes", message.Length.ToString());
                    ProcessMessage(message);

                    bytesRead = 0;
                    message = string.Empty;
                }
            }
            catch (Exception e)
            {
                //TODO: clean that up
                if (Core.Server.Status == Gameserver.ServerStatus.Online) Logger.Log(LogLevel.Warning, "Rcon disconnected. {0}", e.ToString());
            }
            reader.Close();
            writer.Close();
            client.Close();
            running = false;

            InvokeEvent(Disconnected, this, new EventArgs());
        }

        /// <summary>
        /// Processes messages received by the server
        /// </summary>
        /// <param name="message">messaged received from server</param>
        private void ProcessMessage(string message)
        {
            if (message.Length > 0)
            {
                if (HandleChat(message)) return;
                if (HandleStatusMessage(message)) return;
            }

            lock (rxLock)
            {
                lastMessage = message;
            }

            string lastMessageTemp;

            DateTime start = DateTime.Now;
            do
            {
                if ((DateTime.Now - start).TotalMilliseconds < PacketTimeout)
                {
                    Thread.Sleep(SLEEP);
                }
                else
                {
                    Logger.Log(LogLevel.Warning, "Rcon response was not processed in time, dropping it.");
                    return;
                }
                lock (rxLock)
                {
                    lastMessageTemp = lastMessage;
                }
            } while (lastMessageTemp != null);
        }

        /// <summary>
        /// Checks if the specified message is a chat-message. If so, the message is processed.
        /// <para>If the message was processed, true is returned.</para>
        /// </summary>
        /// <param name="message">messaged received from server</param>
        private bool HandleChat(string message)
        {
            if (message[0] != '\t') return false;

            string[] cc = message.Split('\t');
            if (cc.Length < 2)
            {
                Logger.Log(LogLevel.Warning, "Received invalid chat fragment. ({0})", message);
            }
            else
            {
                InvokeEvent(ChatInput, this, new RconChatEventArgs(new ChatMessage(cc[2], cc[1])));
            }
            return true;
        }

        /// <summary>
        /// Checks if the specified message is a status-message. If so, the message is processed.
        /// <para>If the message was processed, true is returned.</para>
        /// </summary>
        /// <param name="message">messaged received from server</param>
        private bool HandleStatusMessage(string message)
        {
            switch (message)
            {
                case STATUS_MESSAGE_GAME_HAS_ENDED:
                    InvokeEvent(GameEnded, this, null);
                    return true;
                default:
                    return false;
            }
        }
        #endregion
    }
}