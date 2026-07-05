using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BlitzRelay.Ngo
{
	[AddComponentMenu("Netcode/Blitz Relay Transport")]
	public sealed class BlitzRelayTransport : NetworkTransport
	{
		[Header("Relay Connection")]
		[SerializeField]
		[Tooltip("Public IP address or hostname of the relay server.")]
		private string relayAddress = "127.0.0.1";

		[SerializeField]
		[Tooltip("Port of the relay server.")]
		private ushort relayPort = 7770;

		[SerializeField]
		[Tooltip("Connection key to use for relay connections.")]
		private string relayKey = string.Empty;

		[SerializeField]
		[Tooltip("Room code to join or claim on the relay server. For host-created rooms, the relay assigns this after room creation.")]
		private string roomCode = string.Empty;

		[SerializeField]
		[Tooltip("While true, forces sockets to send data directly to interface without routing.")]
		private bool doNotRoute;

		[Header("Server")]
		[SerializeField]
		[Tooltip("Maximum number of players which may be connected at once.")]
		[Range(1, 8192)]
		private int maximumClients = 4096;

		[SerializeField]
		[Tooltip("LiteNetLib inactivity timeout in milliseconds.")]
		[Min(1)]
		private int disconnectTimeoutMilliseconds = 30000;

		private const int MaximumUdpMtu = 1350;

		private readonly Queue<TransportEventData> _transportEvents = new();

		private TransportEventData? _lastPolledEvent;

		private string _roomHostToken = string.Empty;

		private string _pendingPromotedClaimToken = string.Empty;

		private int _pendingPromotedMaximumClients;

		public readonly HostSocket ServerSocket = new();

		public readonly ClientSocket ClientSocket = new();

		public override ulong ServerClientId
		{
			get => 0UL;
		}

		public bool IsRelayHostAvailable { get; private set; } = true;

		public bool HasPendingHostPromotion
		{
			get => !string.IsNullOrWhiteSpace(_pendingPromotedClaimToken);
		}

		internal bool DoNotRoute
		{
			get => doNotRoute;
		}

		public event Action<bool> OnRelayHostAvailabilityChanged;

		public event Action<HostPromotion> OnHostPromotionReceived;

		public override void Initialize(NetworkManager networkManager = null)
		{
			InitialiseSockets();
		}

		public override bool StartClient()
		{
			InitialiseSockets();

			UpdateTimeout();

			return ClientSocket.StartConnection(relayAddress, relayPort, relayKey, roomCode);
		}

		public override bool StartServer()
		{
			InitialiseSockets();

			UpdateTimeout();

			int requestedMaximumClients = _pendingPromotedMaximumClients > 0 ? _pendingPromotedMaximumClients : maximumClients;

			bool started = ServerSocket.StartConnection(relayAddress, relayPort, relayKey, requestedMaximumClients, _pendingPromotedClaimToken, roomCode);

			return started;
		}

		public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
		{
			byte gameChannel = SocketBase.GetGameChannel(networkDelivery);

			if (clientId == ServerClientId)
			{
				ClientSocket.SendToServer(gameChannel, payload);

				return;
			}

			ServerSocket.SendToClient(gameChannel, payload, clientId);
		}

		public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
		{
			DisposeLastPolledEvent();

			if (!_transportEvents.TryDequeue(out TransportEventData transportEvent))
			{
				clientId = 0;

				payload = ArraySegment<byte>.Empty;

				receiveTime = 0;

				return NetworkEvent.Nothing;
			}

			clientId = transportEvent.ClientId;

			receiveTime = transportEvent.ReceiveTime;

			if (transportEvent is { EventType: NetworkEvent.Data, Packet: { } packet })
			{
				payload = packet.ToArraySegment();

				_lastPolledEvent = transportEvent;
			}
			else
			{
				payload = ArraySegment<byte>.Empty;

				transportEvent.Dispose();
			}

			return transportEvent.EventType;
		}

		public override void DisconnectRemoteClient(ulong clientId)
		{
			if (clientId == ServerClientId) return;

			ServerSocket.DisconnectRemoteClient(clientId);
		}

		public override void DisconnectLocalClient()
		{
			SetDisconnectEvent(DisconnectEvents.Disconnected);

			ClientSocket.StopConnection(false);
		}

		public override ulong GetCurrentRtt(ulong clientId)
		{
			return clientId == ServerClientId ? ClientSocket.GetRelayRtt() : ServerSocket.GetRelayRtt();
		}

		public override void Shutdown()
		{
			ClientSocket.StopConnection(false);

			ServerSocket.StopConnection();

			_roomHostToken = string.Empty;

			HandleRelayHostAvailability(false);

			DisposeLastPolledEvent();

			while (_transportEvents.TryDequeue(out TransportEventData transportEvent))
			{
				transportEvent.Dispose();
			}
		}

		public string GetRelayAddress()
		{
			return relayAddress;
		}

		public void SetRelayAddress(string newRelayAddress)
		{
			relayAddress = newRelayAddress;
		}

		public ushort GetRelayPort()
		{
			return relayPort;
		}

		public void SetRelayPort(ushort newRelayPort)
		{
			relayPort = newRelayPort;
		}

		public string GetRelayKey()
		{
			return relayKey;
		}

		public void SetRelayKey(string newRelayKey)
		{
			relayKey = newRelayKey ?? string.Empty;
		}

		public string GetRoomCode()
		{
			return roomCode;
		}

		public void SetRoomCode(string newRoomCode)
		{
			roomCode = newRoomCode ?? string.Empty;
		}

		public string GetRoomHostToken()
		{
			return _roomHostToken;
		}

		public void SetRoomHostToken([NotNull] string newRoomHostToken)
		{
			_roomHostToken = newRoomHostToken;
		}

		public int GetMaximumClients()
		{
			return maximumClients;
		}

		public void SetMaximumClients(int newMaximumClients)
		{
			maximumClients = newMaximumClients;
		}

		public void ClearPendingHostPromotion()
		{
			_pendingPromotedClaimToken = string.Empty;

			_pendingPromotedMaximumClients = 0;
		}

		internal void HandleClientHostPromoted(string promotedRoomCode, int promotedMaximumClients, string claimToken)
		{
			roomCode = promotedRoomCode;

			maximumClients = promotedMaximumClients;

			_pendingPromotedMaximumClients = promotedMaximumClients;

			_pendingPromotedClaimToken = claimToken ?? string.Empty;

			HandleRelayHostAvailability(false);

			OnHostPromotionReceived?.Invoke(new HostPromotion(promotedRoomCode, promotedMaximumClients));
		}

		internal void HandleRelayHostAvailability(bool isAvailable)
		{
			if (IsRelayHostAvailable == isAvailable) return;

			IsRelayHostAvailable = isAvailable;

			OnRelayHostAvailabilityChanged?.Invoke(isAvailable);
		}

		internal void EnqueueConnect(ulong clientId)
		{
			_transportEvents.Enqueue(TransportEventData.Create(NetworkEvent.Connect, clientId, Time.realtimeSinceStartup));
		}

		internal void EnqueueDisconnect(ulong clientId)
		{
			_transportEvents.Enqueue(TransportEventData.Create(NetworkEvent.Disconnect, clientId, Time.realtimeSinceStartup));
		}

		internal void EnqueueData(ulong clientId, DataPacket packet)
		{
			_transportEvents.Enqueue(TransportEventData.CreateData(clientId, packet, Time.realtimeSinceStartup));
		}

		internal void EnqueueTransportFailure()
		{
			_transportEvents.Enqueue(TransportEventData.Create(NetworkEvent.TransportFailure, ServerClientId, Time.realtimeSinceStartup));
		}

		internal void SetRelayDisconnectEvent(DisconnectEvents disconnectEvent, string message)
		{
			SetDisconnectEvent(disconnectEvent, message);
		}

		protected override void OnEarlyUpdate()
		{
			ServerSocket.PollSocket();

			ClientSocket.PollSocket();
		}

		protected override void OnPostLateUpdate()
		{
			ServerSocket.IterateOutgoing();

			ClientSocket.IterateOutgoing();
		}

		private void OnDestroy()
		{
			Shutdown();
		}

		private void InitialiseSockets()
		{
			ServerSocket.Initialise(this, MaximumUdpMtu);

			ClientSocket.Initialise(this, MaximumUdpMtu);
		}

		private void UpdateTimeout()
		{
			ServerSocket.UpdateTimeout(disconnectTimeoutMilliseconds);

			ClientSocket.UpdateTimeout(disconnectTimeoutMilliseconds);
		}

		private void DisposeLastPolledEvent()
		{
			if (_lastPolledEvent == null) return;

			_lastPolledEvent?.Dispose();

			_lastPolledEvent = null;
		}
	}
}
