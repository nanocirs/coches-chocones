using System.Collections.Generic;
using System.Linq;
using Godot;

public partial class MultiplayerManager : Singleton<MultiplayerManager> {

    // Main menu lobby signals
    [Signal] public delegate void OnServerCreatedEventHandler();
    [Signal] public delegate void OnServerClosedEventHandler();
    [Signal] public delegate void OnServerCreateErrorEventHandler();
    [Signal] public delegate void OnPlayerConnectedEventHandler(int id, string playerName);
    [Signal] public delegate void OnPlayerDisconnectedEventHandler(int id, string playerName);
    [Signal] public delegate void OnPlayerConnectErrorEventHandler();

    // Game lobby signals
    [Signal] public delegate void OnPlayerLoadedEventHandler(int peerId, int playerId, bool isLocal);
    [Signal] public delegate void OnPlayersReadyEventHandler();
    [Signal] public delegate void OnCountdownEndedEventHandler();
    [Signal] public delegate void OnFinishLineCrossedEventHandler(int playerId);
    [Signal] public delegate void OnCarFinishedEventHandler(int playerId);

    public class PlayerData {
        public string playerName;
        public int playerId; 
        public int currentLap;
        public bool finished;
        public Transform3D carTransform;
        public float carSteering;
    }

    private const int SV_PEER_ID = 1;

    private const string DEFAULT_IP = "127.0.0.1";
    private const int PORT = 42010;
    private const int MAX_CONNECTIONS = 12;

    public static bool connected { get; private set; } = false;
    public static int minimumPlayers { get; private set; } = 2;

    public static Dictionary<int, PlayerData> players = new Dictionary<int, PlayerData>();
    public static int localPlayerId = -1;

    private static int playerIterator = 0;

    // @TODO: This doesn't belong here.
    public string playerName = "QuePasaShavales";

    public override void _SingletonReady() {

        Multiplayer.PeerConnected += PeerConnected;
        Multiplayer.PeerDisconnected += PeerDisconnected;
        Multiplayer.ConnectedToServer += ConnectedToServer;
        Multiplayer.ConnectionFailed += ConnectionFailed;
        Multiplayer.ServerDisconnected += ServerDisconnected;

    }

#region MAIN_MENU_LOBBY

    public static void CreateServer() {

        ENetMultiplayerPeer peer = new ENetMultiplayerPeer();

        Error error = peer.CreateServer(PORT, MAX_CONNECTIONS);

        if (error != Error.Ok) {

            instance.EmitSignal(SignalName.OnServerCreateError);

            GD.PrintErr(error);

            return;

        }
        instance.Multiplayer.MultiplayerPeer = peer;

        connected = true;

        PlayerData newPlayer = new PlayerData();
        newPlayer.playerName = instance.playerName;
        players[SV_PEER_ID] = newPlayer;

        instance.EmitSignal(SignalName.OnServerCreated);
        instance.EmitSignal(SignalName.OnPlayerConnected, SV_PEER_ID, players[SV_PEER_ID].playerName);
        
    }

    public static void JoinServer(string address = "") {
        
        if (address == "") {
            address = DEFAULT_IP;
        }

        ENetMultiplayerPeer peer = new ENetMultiplayerPeer();
        
        Error error = peer.CreateClient(address, PORT);

        if (error != Error.Ok) {

            instance.EmitSignal(SignalName.OnPlayerConnectError);

            GD.PrintErr(error);

            return;

        }

        instance.Multiplayer.MultiplayerPeer = peer;

    }

    public static int GetTotalPlayers() {
        
        return players.Count;

    }

    public static void DisconnectFromServer() {

        int playerId = instance.Multiplayer.GetUniqueId();

        if (players.Count() > 0) {
            
            string peerName = players[playerId].playerName;
            instance.Multiplayer.MultiplayerPeer.Close();

            instance.EmitSignal(SignalName.OnPlayerDisconnected, playerId, peerName);

        }

    }

    public static void LoadMap(string mapFile) {

        string mapPath = "Maps/" + mapFile;

        instance.Rpc("NotifyLoadMap", mapPath);

    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyLoadMap(string mapPath) {

        GameStateMachine.instance.LoadScene(mapPath);

    }

    private void PeerConnected(long id) {
        RpcId(id, "RegisterPlayer", playerName);
    }

    private void PeerDisconnected(long id) {

        string peerName = players[(int)id].playerName;

        if (players[(int)id] != null) {
            players.Remove((int)id);
        }

        EmitSignal(SignalName.OnPlayerDisconnected, (int)id, peerName);

    }

    private void ConnectedToServer() {

        int playerId = Multiplayer.GetUniqueId();

        PlayerData newPlayer = new PlayerData();
        newPlayer.playerName = playerName;
        players[playerId] = newPlayer;

        EmitSignal(SignalName.OnPlayerConnected, playerId, playerName);

        connected = true;

    }

    private void ConnectionFailed() {

        Multiplayer.MultiplayerPeer = null;

        connected = false;

        GD.Print("Failed to connect to Server");
        
    }

    private void ServerDisconnected() {

        players.Clear();

        EmitSignal(SignalName.OnServerClosed);

        connected = false;

        GD.Print("Disconnected from Server");
        
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RegisterPlayer(string newPlayerName) {

        int playerId = Multiplayer.GetRemoteSenderId();

        PlayerData newPlayer = new PlayerData();
        newPlayer.playerName = newPlayerName;
        players[playerId] = newPlayer;

        EmitSignal(SignalName.OnPlayerConnected, playerId, newPlayerName);

    }

#endregion

#region GAME_LOBBY

    public static void NotifyMapLoaded() {

        if (instance.Multiplayer.IsServer()) {
    
            localPlayerId = playerIterator++;

            players[SV_PEER_ID] = RestartPlayer(0, 0, false);

            instance.CallDeferred("OnPlayerLoadedEmit", SV_PEER_ID, 0);

        }
        else {
            instance.RpcId(SV_PEER_ID, "NotifyPlayerLoaded");
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyPlayerLoaded() {

        int clientPlayerId = playerIterator++;

        players[Multiplayer.GetRemoteSenderId()] = RestartPlayer(clientPlayerId, 0, false);

        RpcId(Multiplayer.GetRemoteSenderId(), "SetPlayerId", clientPlayerId);

        foreach (var tuple in players) {
            Rpc("UpdatePlayerList",tuple.Key, tuple.Value.playerId);
        }

    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetPlayerId(int playerId) {
        
        localPlayerId = playerId;

        CallDeferred("OnPlayerLoadedEmit", SV_PEER_ID, 0);   // Let client know server is ready.

        Rpc("EmitPlayerLoaded", localPlayerId);

    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void UpdatePlayerList(int peerId, int playerId) {

        players[peerId] = RestartPlayer(playerId, 0, false);

    }


    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EmitPlayerLoaded(int playerId) {

        CallDeferred("OnPlayerLoadedEmit", Multiplayer.GetRemoteSenderId(), playerId);

    }

    private void OnPlayerLoadedEmit(int peerId, int playerId) {
        
        if (localPlayerId == playerId) {
            EmitSignal(SignalName.OnPlayerLoaded, peerId, playerId, true);
        }
        else {
            EmitSignal(SignalName.OnPlayerLoaded, peerId, playerId, false);
        }
    }

    private static PlayerData RestartPlayer(int playerId, int currentLap, bool finished) {
        
        return new PlayerData {
            playerId = playerId,
            currentLap = currentLap,
            finished = finished
        };
        
    }

#endregion

#region IN_GAME

    // PLAYERS READY

    public static void PlayersReady() {

        if (instance.Multiplayer.IsServer()) {
            instance.Rpc("OnPlayersReadyEmit");               
        }
        else {
            instance.RpcId(SV_PEER_ID, "NotifyPlayersReady");
        }

    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyPlayersReady() {

        Rpc("OnPlayersReadyEmit");               

    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void OnPlayersReadyEmit() {

        EmitSignal(SignalName.OnPlayersReady);

    }

    // END COUNTDOWN

    public static void EndCountdown() {

        if (instance.Multiplayer.IsServer()) {
            instance.Rpc("OnCountdownEndedEmit");
        }

    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void OnCountdownEndedEmit() {

        EmitSignal(SignalName.OnCountdownEnded);

    }

    // FINISH LINE CROSSED

    public static void FinishLineCrossed() {

        if (instance.Multiplayer.IsServer()) {
            instance.Rpc("OnFinishLineCrossedEmit", SV_PEER_ID);
        }
        else {
            instance.RpcId(SV_PEER_ID, "NotifyFinishLineCrossed");
        }

    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void NotifyFinishLineCrossed() {

        Rpc("OnFinishLineCrossedEmit", Multiplayer.GetRemoteSenderId());
        
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void OnFinishLineCrossedEmit(int peerId) {

        players[peerId].currentLap++;

        EmitSignal(SignalName.OnFinishLineCrossed, players[peerId].playerId);

    }

    // SEND PLAYER TRANSFORM

    public static void NotifyPlayerTransform(Transform3D globalTransform, float steering) {
       
        if (instance.Multiplayer.IsServer()) {
            instance.Rpc("UpdateTransforms", SV_PEER_ID, globalTransform, steering);               
        }
        else {
            instance.RpcId(SV_PEER_ID, "NotifyTransform", globalTransform, steering);
        }

    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void NotifyTransform(Transform3D globalTransform, float steering) {

        Rpc("UpdateTransforms", Multiplayer.GetRemoteSenderId(), globalTransform, steering);               

    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void UpdateTransforms(int peerId, Transform3D globalTransform, float steering) {

        UpdateCarState(peerId, globalTransform, steering);
        
    }

    // CAR FINISHED
    public static void CarFinished() {

    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void OnCarFinishedEmit() {

        players[Multiplayer.GetRemoteSenderId()].currentLap++;

        EmitSignal(SignalName.OnCarFinished, players[Multiplayer.GetRemoteSenderId()].playerId);

    }
 
    // UPDATE CAR STATE

    public static void UpdateCarState(int peerId, Transform3D carTransform, float steering) {
        players[peerId].carTransform = carTransform;
        players[peerId].carSteering = steering;
    }

#endregion

}
