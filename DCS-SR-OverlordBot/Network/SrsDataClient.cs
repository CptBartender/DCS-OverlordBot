﻿using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using RurouniJones.DCS.OverlordBot.Settings;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Setting;
using Easy.MessageHub;
using Newtonsoft.Json;
using NLog;

namespace RurouniJones.DCS.OverlordBot.Network
{
    public class SrsDataClient
    {
        public delegate void ConnectCallback(bool result);

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private volatile bool _stop;

        public static string ServerVersion = "Unknown";
        private ConnectCallback _connectionCallback;
        private IPEndPoint _serverEndpoint;
        private TcpClient _tcpClient;

        private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;
        private readonly Client _mainClient;

        private readonly string _guid;

        private const int MaxDecodeErrors = 5;

        public static volatile bool ApplicationStopped = false;

        public SrsDataClient(Client mainClient)
        {
            _mainClient = mainClient;
            _guid = mainClient.ShortGuid;
        }

        public void TryConnect(IPEndPoint endpoint, ConnectCallback callback)
        {
            _connectionCallback = callback;
            _serverEndpoint = endpoint;

            var tcpThread = new Thread(Connect) {Name = "SRS Data"};

            tcpThread.Start();
        }

        public void ConnectExternalAwacsMode()
        {
            if (_mainClient.ExternalAwacsModeConnected)
            {
                return;
            }

            _mainClient.ExternalAwacsModeSelected = true;

            var sideInfo = _mainClient.PlayerCoalitionLocationMetadata;
            sideInfo.name = _mainClient.LastSeenName;

            var message = new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = sideInfo.side,
                    Name = sideInfo.name,
                    LatLngPosition = sideInfo.LngLngPosition,
                    ClientGuid = _guid
                },
                ExternalAWACSModePassword = _mainClient.ExternalAwacsModePassword,
                MsgType = NetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD
            };

            SendToServer(message);
        }

        public void DisconnectExternalAwacsMode()
        {
            _mainClient.ExternalAwacsModeConnected = false;
            _mainClient.PlayerCoalitionLocationMetadata.side = 0;
            _mainClient.PlayerCoalitionLocationMetadata.name = "";
            _mainClient.DcsPlayerRadioInfo.name = "";
            _mainClient.DcsPlayerRadioInfo.LastUpdate = 0;
            _mainClient.LastSent = 0;
        }

        private void Connect()
        {
            var connectionError = false;

            using (_tcpClient = new TcpClient())
            {
                try
                {
                    _tcpClient.SendTimeout = 10000;
                    _tcpClient.NoDelay = true;

                    // Wait for 10 seconds before aborting connection attempt - no SRS server running/port opened in that case
                    _tcpClient.ConnectAsync(_serverEndpoint.Address, _serverEndpoint.Port).Wait(TimeSpan.FromSeconds(10));

                    if (_tcpClient.Connected)
                    {
                        _tcpClient.NoDelay = true;

                        _connectionCallback(true);
                        ClientSyncLoop();
                    }
                    else
                    {
                        Logger.Error($"Failed to connect to server @ {_serverEndpoint}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not connect to server");
                }
            }
            _connectionCallback(false);
        }

        private void SendAwacsRadioInformation()
        {
            _mainClient.LastSent = 0;
            _mainClient.ExternalAwacsModeConnected = true;

            var sideInfo = _mainClient.PlayerCoalitionLocationMetadata;

            var message = new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = sideInfo.side,
                    Name = sideInfo.name,
                    ClientGuid = _guid,
                    RadioInfo = _mainClient.DcsPlayerRadioInfo,
                    LatLngPosition = sideInfo.LngLngPosition
                },
                MsgType = NetworkMessage.MessageType.RADIO_UPDATE
            };

            SendToServer(message);
        }

        private void ClientSyncLoop()
        {
            //clear the clients list
            _mainClient.Clear();
            var decodeErrors = 0; //if the JSON is unreadable - new version likely

            using (var reader = new StreamReader(_tcpClient.GetStream(), Encoding.UTF8))
            {
                try
                {
                    var sideInfo = _mainClient.PlayerCoalitionLocationMetadata;
                    //start the loop off by sending a SYNC Request
                    SendToServer(new NetworkMessage
                    {
                        Client = new SRClient
                        {
                            Coalition = sideInfo.side,
                            Name = sideInfo.name.Length > 0 ? sideInfo.name : _mainClient.LastSeenName,
                            LatLngPosition = sideInfo.LngLngPosition,
                            ClientGuid = _guid
                        },
                        MsgType = NetworkMessage.MessageType.SYNC
                    });

                    string line;
                    while ((line = reader.ReadLine()) != null && ApplicationStopped == false)
                    {
                        try
                        {
                            var serverMessage = JsonConvert.DeserializeObject<NetworkMessage>(line);
                            decodeErrors = 0; //reset counter
                            if (serverMessage != null)
                            {
                                Logger.Trace($"Message {serverMessage.MsgType} received: {line}");
                                switch (serverMessage.MsgType)
                                {
                                    case NetworkMessage.MessageType.PING:
                                        // Do nothing for now
                                        break;
                                    case NetworkMessage.MessageType.RADIO_UPDATE:
                                    case NetworkMessage.MessageType.UPDATE:

                                        if (serverMessage.ServerSettings != null)
                                        {
                                            _serverSettings.Decode(serverMessage.ServerSettings);
                                        }

                                        if (_mainClient.ContainsKey(serverMessage.Client.ClientGuid))
                                        {
                                            var srClient = _mainClient[serverMessage.Client.ClientGuid];
                                            var updatedSrClient = serverMessage.Client;
                                            if (srClient != null)
                                            {
                                                srClient.LastUpdate = DateTime.Now.Ticks;
                                                srClient.Name = updatedSrClient.Name;
                                                srClient.Coalition = updatedSrClient.Coalition;

                                                srClient.LatLngPosition = updatedSrClient.LatLngPosition;

                                                if (updatedSrClient.RadioInfo != null)
                                                {
                                                    srClient.RadioInfo = updatedSrClient.RadioInfo;
                                                    srClient.RadioInfo.LastUpdate = DateTime.Now.Ticks;
                                                }
                                                else
                                                {
                                                    //radio update but null RadioInfo means no change
                                                    if (serverMessage.MsgType ==
                                                        NetworkMessage.MessageType.RADIO_UPDATE &&
                                                        srClient.RadioInfo != null)
                                                    {
                                                        srClient.RadioInfo.LastUpdate = DateTime.Now.Ticks;
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            var connectedClient = serverMessage.Client;
                                            connectedClient.LastUpdate = DateTime.Now.Ticks;

                                            //init with LOS true so you can hear them incase of bad DCS install where
                                            //LOS isnt working
                                            connectedClient.LineOfSightLoss = 0.0f;
                                            //0.0 is NO LOSS therefore full Line of sight

                                            _mainClient[serverMessage.Client.ClientGuid] = connectedClient;
                                        }

                                        if (_mainClient.ExternalAwacsModeSelected &&
                                            !_serverSettings.GetSettingAsBool(ServerSettingsKeys.EXTERNAL_AWACS_MODE))
                                        {
                                            Logger.Error($"This server does not support External Awacs mode");
                                            _mainClient.Disconnect();
                                        }

                                        break;
                                    case NetworkMessage.MessageType.SYNC:
                                        //check server version
                                        if (serverMessage.Version == null)
                                        {
                                            Logger.Error("Disconnecting Unversioned Server");
                                            _mainClient.Disconnect();
                                            break;
                                        }

                                        var serverVersion = Version.Parse(serverMessage.Version);
                                        var protocolVersion = Version.Parse(UpdaterChecker.MINIMUM_PROTOCOL_VERSION);

                                        ServerVersion = serverMessage.Version;

                                        if (serverVersion < protocolVersion)
                                        {
                                            Logger.Error($"Server version ({serverMessage.Version}) older than minimum procotol version ({UpdaterChecker.MINIMUM_PROTOCOL_VERSION}) - disconnecting");

                                            ShowVersionMistmatchWarning(serverMessage.Version);

                                            _mainClient.Disconnect();
                                            break;
                                        }

                                        if (serverMessage.Clients != null)
                                        {
                                            foreach (var client in serverMessage.Clients)
                                            {
                                                client.LastUpdate = DateTime.Now.Ticks;
                                                //init with LOS true so you can hear them incase of bad DCS install where
                                                //LOS isnt working
                                                client.LineOfSightLoss = 0.0f;
                                                //0.0 is NO LOSS therefore full Line of sight
                                                _mainClient[client.ClientGuid] = client;
                                            }
                                        }
                                        //add server settings
                                        _serverSettings.Decode(serverMessage.ServerSettings);

                                        if (_mainClient.ExternalAwacsModeSelected &&
                                            !_serverSettings.GetSettingAsBool(ServerSettingsKeys.EXTERNAL_AWACS_MODE))
                                        {
                                            Logger.Error($"This server does not support External Awacs mode");
                                            _mainClient.Disconnect();
                                        }

                                        break;

                                    case NetworkMessage.MessageType.SERVER_SETTINGS:

                                        _serverSettings.Decode(serverMessage.ServerSettings);
                                        ServerVersion = serverMessage.Version;

                                        if (_mainClient.ExternalAwacsModeSelected &&
                                            !_serverSettings.GetSettingAsBool(ServerSettingsKeys.EXTERNAL_AWACS_MODE))
                                        {
                                            Logger.Error($"This server does not support External Awacs mode");
                                            _mainClient.Disconnect();
                                        }
                                        break;
                                    case NetworkMessage.MessageType.CLIENT_DISCONNECT:

                                        _mainClient.TryRemove(serverMessage.Client.ClientGuid, out var outClient);

                                        if (outClient != null)
                                        {
                                            MessageHub.Instance.Publish(outClient);
                                        }

                                        break;
                                    case NetworkMessage.MessageType.VERSION_MISMATCH:
                                        Logger.Error($"Version Mismatch Between Client ({UpdaterChecker.VERSION}) & Server ({serverMessage.Version}) - Disconnecting");
                                        _mainClient.Disconnect();
                                        break;
                                    case NetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD:
                                        if (serverMessage.Client.Coalition > 0)
                                        {
                                            Logger.Info("External AWACS mode authentication succeeded, coalition {0}", serverMessage.Client.Coalition == 1 ? "red" : "blue");
                                            _mainClient.PlayerCoalitionLocationMetadata.side = serverMessage.Client.Coalition;
                                            _mainClient.PlayerCoalitionLocationMetadata.name = _mainClient.LastSeenName;
                                            _mainClient.DcsPlayerRadioInfo.name = _mainClient.LastSeenName;
                                            SendAwacsRadioInformation();
                                        }
                                        else
                                        {
                                            Logger.Info("External AWACS mode authentication failed");
                                            _mainClient.Disconnect();
                                        }
                                        break;
                                    default:
                                        Logger.Error("Received unknown " + line);
                                        break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, $"Error decoding message from server: {line}");
                            decodeErrors++;
                            if (!_stop)
                            {
                                Logger.Error(ex, "Client exception reading from socket ");
                            }

                            if (decodeErrors <= MaxDecodeErrors) continue;
                            Logger.Error("Too many errors decoding server messagse. disconnecting");
                            _mainClient.Disconnect();
                            break;
                        }

                        // do something with line
                    }
                }
                catch (Exception ex)
                {
                    if (!_stop)
                    {
                        Logger.Error(ex, "Client exception reading - Disconnecting ");
                    }
                }
            }

            //disconnected - reset DCS Info
            _mainClient.DcsPlayerRadioInfo.LastUpdate = 0;

            //clear the clients list
            _mainClient.Clear();
            _mainClient.Disconnect();
        }

        private static void ShowVersionMistmatchWarning(string serverVersion)
        {
            MessageBox.Show("The SRS server you're connecting to is incompatible with this Client. " +
                            "\n\nMake sure to always run the latest version of the SRS Server & Client" +
                            $"\n\nServer Version: {serverVersion}" +
                            $"\nClient Version: {UpdaterChecker.VERSION}" +
                            $"\nMinimum Version: {UpdaterChecker.MINIMUM_PROTOCOL_VERSION}",
                            "SRS Server Incompatible",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
        }

        private void SendToServer(NetworkMessage message)
        {
            try
            {

                message.Version = UpdaterChecker.VERSION;

                var json = message.Encode();

                var bytes = Encoding.UTF8.GetBytes(json);
                try
                {
                    _tcpClient.GetStream().Write(bytes, 0, bytes.Length);
                    Logger.Trace($"Message {message.MsgType} sent: {json}");

                } catch (ObjectDisposedException ex)
                {
                    Logger.Debug(ex, $"Tried writing message type {message.MsgType} to a disposed TcpClient");
                }
                //Need to flush?
            }
            catch (Exception ex)
            {
                if (!_stop)
                {
                    Logger.Error(ex, $"Client exception sending message type {message.MsgType} to server");
                }

                _mainClient.Disconnect();
            }
        }

        //implement IDispose? To close stuff properly?
        public void Disconnect()
        {
            _stop = true;

            _tcpClient?.Close(); // this'll stop the socket blocking

            Logger.Error("Disconnecting data connection from server");
            _mainClient.IsDataConnected = false;

        }
    }
}