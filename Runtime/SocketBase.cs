using BlitzRelay.Ngo.LiteNetLib;
using BlitzRelay.Protocol;
using System;
using System.Collections.Generic;
using Unity.Netcode;

namespace BlitzRelay.Ngo
{
	public abstract class SocketBase
	{
		protected string RelayAddress = string.Empty;

		protected ushort RelayPort;

		protected string RelayKey = string.Empty;

		protected string RoomCode = string.Empty;

		protected int DisconnectTimeoutMilliseconds = 30000;

		protected int Mtu;

		protected NetPeer RelayPeer;

		protected BlitzRelayTransport Transport;

		protected readonly Queue<DataPacket> OutgoingPackets = new();

		public NetManager SocketNetManager { get; protected set; }

		public RelaySocketState SocketState { get; protected set; } = RelaySocketState.Stopped;

		internal void Initialise(BlitzRelayTransport transport, int mtu)
		{
			Transport = transport ?? throw new ArgumentNullException(nameof(transport));

			Mtu = mtu;
		}

		internal void UpdateTimeout(int timeoutMilliseconds)
		{
			DisconnectTimeoutMilliseconds = timeoutMilliseconds <= 0 ? int.MaxValue : timeoutMilliseconds;

			if (SocketNetManager != null) SocketNetManager.DisconnectTimeout = DisconnectTimeoutMilliseconds;
		}

		internal void PollSocket()
		{
			SocketNetManager?.PollEvents();
		}

		internal ulong GetRelayRtt()
		{
			return RelayPeer == null ? 0UL : (ulong)Math.Max(0, RelayPeer.RoundTripTime);
		}

		protected bool StartRelayConnection
		(
			EventBasedNetListener listener,
			string address,
			ushort port,
			string key,
			int disconnectTimeoutMilliseconds,
			int mtu,
			bool doNotRoute
		)
		{
			NetManager netManager = null;

			NetPeer relayPeer = null;

			Exception startupException = null;

			try
			{
				netManager = new NetManager(listener)
				{
					DisconnectTimeout = disconnectTimeoutMilliseconds,

					DontRoute = doNotRoute,

					MtuOverride = mtu,
				};

				if (netManager.Start())
				{
					relayPeer = netManager.Connect(address, port, key);
				}
			}
			catch (Exception exception)
			{
				startupException = exception;
			}

			if (startupException != null)
			{
				netManager?.Stop(false);

				SocketState = RelaySocketState.Stopped;

				Transport.SetRelayDisconnectEvent(NetworkTransport.DisconnectEvents.ProtocolError, startupException.Message);

				Transport.EnqueueTransportFailure();

				return false;
			}

			if (relayPeer == null)
			{
				netManager.Stop(false);

				SocketState = RelaySocketState.Stopped;

				Transport.SetRelayDisconnectEvent(NetworkTransport.DisconnectEvents.ProtocolError, "Failed to start relay socket.");

				Transport.EnqueueTransportFailure();

				return false;
			}

			SocketNetManager = netManager;

			RelayPeer = relayPeer;

			return true;
		}

		protected void Send(Queue<DataPacket> queue, byte channelId, ArraySegment<byte> segment, ulong clientId)
		{
			if (SocketState != RelaySocketState.Started) return;

			queue.Enqueue(new DataPacket(clientId, segment, channelId));
		}

		internal static byte GetGameChannel(NetworkDelivery networkDelivery)
		{
			return networkDelivery switch
			{
				NetworkDelivery.Unreliable or NetworkDelivery.UnreliableSequenced => (byte)GameChannel.Unreliable,

				_ => (byte)GameChannel.Reliable,
			};
		}

		protected static DeliveryMethod GetDeliveryMethod(byte gameChannel)
		{
			return MessageCodec.IsUnreliableGameChannel(gameChannel) ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered;
		}

		protected void DisposeOutgoingPackets()
		{
			while (OutgoingPackets.TryDequeue(out DataPacket packet))
			{
				packet.Dispose();
			}
		}

		protected void StopSocket(bool sendDisconnectMessages)
		{
			SocketNetManager?.Stop(sendDisconnectMessages);

			SocketNetManager = null;

			RelayPeer = null;

			SocketState = RelaySocketState.Stopped;
		}
	}
}
