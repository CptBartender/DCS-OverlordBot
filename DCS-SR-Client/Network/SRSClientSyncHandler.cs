﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Settings;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.DCSState;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network;
using Easy.MessageHub;
using Newtonsoft.Json;
using NLog;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Network
{
    public class SRSClientSyncHandler
    {
        public delegate void ConnectCallback(bool result, bool connectionError, string connection);
        public delegate void ExternalAWACSModeConnectCallback(bool result, int coalition);
        public delegate void UpdateUICallback();

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private volatile bool _stop = false;

        public static string ServerVersion = "Unknown";
        private readonly string _guid;
        private ConnectCallback _callback;
        private ExternalAWACSModeConnectCallback _externalAWACSModeCallback;
        private UpdateUICallback _updateUICallback;
        private IPEndPoint _serverEndpoint;
        private TcpClient _tcpClient;

        private readonly ClientStateSingleton _clientStateSingleton = ClientStateSingleton.Instance;
        private readonly SyncedServerSettings _serverSettings = SyncedServerSettings.Instance;
        private readonly ConnectedClientsSingleton _clients = ConnectedClientsSingleton.Instance;


        private static readonly int MAX_DECODE_ERRORS = 5;


        public SRSClientSyncHandler(string guid, UpdateUICallback uiCallback)
        {
            _guid = guid;
            _updateUICallback = uiCallback;
        }


        public void TryConnect(IPEndPoint endpoint, ConnectCallback callback)
        {
            _callback = callback;
            _serverEndpoint = endpoint;

            var tcpThread = new Thread(Connect);
            tcpThread.Start();
        }

        public void ConnectExternalAWACSMode(string password, ExternalAWACSModeConnectCallback callback)
        {
            if (_clientStateSingleton.ExternalAWACSModelSelected)
            {
                return;
            }

            _externalAWACSModeCallback = callback;

            var sideInfo = _clientStateSingleton.PlayerCoaltionLocationMetadata;
            sideInfo.name = _clientStateSingleton.LastSeenName;
            SendToServer(new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = sideInfo.side,
                    Name = sideInfo.name,
                    LatLngPosition = sideInfo.LngLngPosition,
                    ClientGuid = _guid
                },
                ExternalAWACSModePassword = password,
                MsgType = NetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD
            });
        }

        public void DisconnectExternalAWACSMode()
        {
            if (!_clientStateSingleton.ExternalAWACSModelSelected)
            {
                return;
            }

            CallExternalAWACSModeOnMain(false, 0);
        }

        private void Connect()
        {
            bool connectionError = false;

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

                        CallOnMain(true);
                        ClientSyncLoop();
                    }
                    else
                    {
                        Logger.Error($"Failed to connect to server @ {_serverEndpoint.ToString()}");

                        // Signal disconnect including an error
                        connectionError = true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not connect to server");
                    connectionError = true;
                }
            }

            //disconnect callback
            CallOnMain(false, connectionError);
        }

        private void ClientRadioUpdated()
        {
            Logger.Debug("Sending Radio Update to Server");
            var sideInfo = _clientStateSingleton.PlayerCoaltionLocationMetadata;
            SendToServer(new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = sideInfo.side,
                    Name = sideInfo.name,
                    ClientGuid = _guid,
                    RadioInfo = _clientStateSingleton.DcsPlayerRadioInfo,
                    LatLngPosition = sideInfo.LngLngPosition
                },
                MsgType = NetworkMessage.MessageType.RADIO_UPDATE
            });
        }

        private void ClientCoalitionUpdate()
        {
            var sideInfo = _clientStateSingleton.PlayerCoaltionLocationMetadata;
            SendToServer(new NetworkMessage
            {
                Client = new SRClient
                {
                    Coalition = sideInfo.side,
                    Name = sideInfo.name,
                    LatLngPosition = sideInfo.LngLngPosition,
                    ClientGuid = _guid
                },
                MsgType = NetworkMessage.MessageType.UPDATE
            });
        }

        private void CallOnMain(bool result, bool connectionError = false)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(DispatcherPriority.Background,
                    new ThreadStart(delegate { _callback(result, connectionError, _serverEndpoint.ToString()); }));
            }
            catch (Exception ex)
            {
                 Logger.Error(ex, "Failed to update UI after connection callback (result {result}, connectionError {connectionError})", result, connectionError);
            }
        }

        private void CallExternalAWACSModeOnMain(bool result, int coalition)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(DispatcherPriority.Background,
                    new ThreadStart(delegate { _externalAWACSModeCallback(result, coalition); }));
            }
            catch (Exception ex)
            {
            }
        }

        private void CallUpdateUIOnMain()
        {
            try
            {
                Application.Current.Dispatcher.Invoke(DispatcherPriority.Background,
                    new ThreadStart(delegate { _updateUICallback(); }));
            }
            catch (Exception ex)
            {
            }
        }

        private void ClientSyncLoop()
        {
            //clear the clients list
            _clients.Clear();
            int decodeErrors = 0; //if the JSON is unreadable - new version likely

            using (var reader = new StreamReader(_tcpClient.GetStream(), Encoding.UTF8))
            {
                try
                {
                    var sideInfo = _clientStateSingleton.PlayerCoaltionLocationMetadata;
                    //start the loop off by sending a SYNC Request
                    SendToServer(new NetworkMessage
                    {
                        Client = new SRClient
                        {
                            Coalition = sideInfo.side,
                            Name = sideInfo.name.Length > 0 ? sideInfo.name : _clientStateSingleton.LastSeenName,
                            LatLngPosition = sideInfo.LngLngPosition,
                            ClientGuid = _guid
                        },
                        MsgType = NetworkMessage.MessageType.SYNC,
                    });

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        try
                        {
                            var serverMessage = JsonConvert.DeserializeObject<NetworkMessage>(line);
                            decodeErrors = 0; //reset counter
                            if (serverMessage != null)
                            {
                                //Logger.Debug("Received "+serverMessage.MsgType);
                                switch (serverMessage.MsgType)
                                {
                                    case NetworkMessage.MessageType.PING:
                                        // Do nothing for now
                                        break;
                                    case NetworkMessage.MessageType.RADIO_UPDATE:
                                    case NetworkMessage.MessageType.UPDATE:

                                        _serverSettings.Decode(serverMessage.ServerSettings);

                                        if (_clients.ContainsKey(serverMessage.Client.ClientGuid))
                                        {
                                            var srClient = _clients[serverMessage.Client.ClientGuid];
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

                                                // Logger.Debug("Received Update Client: " + NetworkMessage.MessageType.UPDATE + " From: " +
                                                //             srClient.Name + " Coalition: " +
                                                //             srClient.Coalition + " Pos: " + srClient.LatLngPosition);
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

                                            _clients[serverMessage.Client.ClientGuid] = connectedClient;

                                            // Logger.Debug("Received New Client: " + NetworkMessage.MessageType.UPDATE +
                                            //             " From: " +
                                            //             serverMessage.Client.Name + " Coalition: " +
                                            //             serverMessage.Client.Coalition);
                                        }

                                        if (_clientStateSingleton.ExternalAWACSModelSelected &&
                                            !_serverSettings.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE))
                                        {
                                            DisconnectExternalAWACSMode();
                                        }

                                        CallUpdateUIOnMain();

                                        break;
                                    case NetworkMessage.MessageType.SYNC:
                                        // Logger.Info("Recevied: " + NetworkMessage.MessageType.SYNC);

                                        //check server version
                                        if (serverMessage.Version == null)
                                        {
                                            Logger.Error("Disconnecting Unversioned Server");
                                            Disconnect();
                                            break;
                                        }

                                        var serverVersion = Version.Parse(serverMessage.Version);
                                        var protocolVersion = Version.Parse(UpdaterChecker.MINIMUM_PROTOCOL_VERSION);

                                        ServerVersion = serverMessage.Version;

                                        if (serverVersion < protocolVersion)
                                        {
                                            Logger.Error($"Server version ({serverMessage.Version}) older than minimum procotol version ({UpdaterChecker.MINIMUM_PROTOCOL_VERSION}) - disconnecting");

                                            ShowVersionMistmatchWarning(serverMessage.Version);

                                            Disconnect();
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
                                                _clients[client.ClientGuid] = client;
                                            }
                                        }
                                        //add server settings
                                        _serverSettings.Decode(serverMessage.ServerSettings);

                                        if (_clientStateSingleton.ExternalAWACSModelSelected &&
                                            !_serverSettings.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE))
                                        {
                                            DisconnectExternalAWACSMode();
                                        }

                                        CallUpdateUIOnMain();

                                        break;

                                    case NetworkMessage.MessageType.SERVER_SETTINGS:

                                        _serverSettings.Decode(serverMessage.ServerSettings);
                                        ServerVersion = serverMessage.Version;

                                        if (_clientStateSingleton.ExternalAWACSModelSelected &&
                                            !_serverSettings.GetSettingAsBool(Common.Setting.ServerSettingsKeys.EXTERNAL_AWACS_MODE))
                                        {
                                            DisconnectExternalAWACSMode();
                                        }

                                        CallUpdateUIOnMain();

                                        break;
                                    case NetworkMessage.MessageType.CLIENT_DISCONNECT:

                                        SRClient outClient;
                                        _clients.TryRemove(serverMessage.Client.ClientGuid, out outClient);

                                        if (outClient != null)
                                        {
                                            MessageHub.Instance.Publish(outClient);
                                        }

                                        break;
                                    case NetworkMessage.MessageType.VERSION_MISMATCH:
                                        Logger.Error($"Version Mismatch Between Client ({UpdaterChecker.VERSION}) & Server ({serverMessage.Version}) - Disconnecting");

                                        ShowVersionMistmatchWarning(serverMessage.Version);

                                        Disconnect();
                                        break;
                                    case NetworkMessage.MessageType.EXTERNAL_AWACS_MODE_PASSWORD:
                                        if (serverMessage.Client.Coalition == 0)
                                        {
                                            Logger.Info("External AWACS mode authentication failed");

                                            CallExternalAWACSModeOnMain(false, 0);
                                        }
                                        else
                                        {
                                            Logger.Info("External AWACS mode authentication succeeded, coalition {0}", serverMessage.Client.Coalition == 1 ? "red" : "blue");

                                            CallExternalAWACSModeOnMain(true, serverMessage.Client.Coalition);
                                        }
                                        break;
                                    default:
                                        Logger.Error("Recevied unknown " + line);
                                        break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            decodeErrors++;
                            if (!_stop)
                            {
                                Logger.Error(ex, "Client exception reading from socket ");
                            }

                            if (decodeErrors > MAX_DECODE_ERRORS)
                            {
                                ShowVersionMistmatchWarning("unknown");
                                Disconnect();
                                break;
                            }
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
            ClientStateSingleton.Instance.DcsPlayerRadioInfo.LastUpdate = 0;

            //clear the clients list
            _clients.Clear();

            Disconnect();
        }

        private void ShowVersionMistmatchWarning(string serverVersion)
        {
            MessageBox.Show($"The SRS server you're connecting to is incompatible with this Client. " +
                            $"\n\nMake sure to always run the latest version of the SRS Server & Client" +
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

                if (message.MsgType == NetworkMessage.MessageType.RADIO_UPDATE)
                {
                    Logger.Debug("Sending Radio Update To Server: "+ (json));
                }

                var bytes = Encoding.UTF8.GetBytes(json);
                _tcpClient.GetStream().Write(bytes, 0, bytes.Length);
                //Need to flush?
            }
            catch (Exception ex)
            {
                if (!_stop)
                {
                    Logger.Error(ex, "Client exception sending to server");
                }

                Disconnect();
            }
        }

        //implement IDispose? To close stuff properly?
        public void Disconnect()
        {
            _stop = true;

            DisconnectExternalAWACSMode();

            try
            {
                if (_tcpClient != null)
                {
                    _tcpClient.Close(); // this'll stop the socket blocking
                }
            }
            catch (Exception ex)
            {
            }

            Logger.Error("Disconnecting from server");
            ClientStateSingleton.Instance.IsConnected = false;

            //CallOnMain(false);
        }
    }
}