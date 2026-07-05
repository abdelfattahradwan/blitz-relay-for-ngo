using BlitzRelay.Ngo.LiteNetLib;
using BlitzRelay.Protocol;
using System;
using System.Buffers;
using System.Collections.Generic;
using Unity.Netcode;

namespace BlitzRelay.Ngo
{
	public sealed class HostSocket : SocketBase
	{
		private readonly HashSet<ulong> _virtualClients = new();

		private int _maximumClients;

		private string _claimToken = string.Empty;

		private bool _claimExistingRoom;

		internal bool StartConnection(string relayAddress, ushort relayPort, string relayKey, int maximumClients, string claimToken, string roomCode)
		{
			if (SocketState != RelaySocketState.Stopped) StopConnection();

			SocketState = RelaySocketState.Starting;

			RelayAddress = relayAddress;

			RelayPort = relayPort;

			RelayKey = relayKey ?? string.Empty;

			_maximumClients = maximumClients;

			_claimToken = claimToken ?? string.Empty;

			_claimExistingRoom = !string.IsNullOrWhiteSpace(_claimToken);

			RoomCode = _claimExistingRoom ? roomCode : string.Empty;

			_virtualClients.Clear();

			DisposeOutgoingPackets();

			EventBasedNetListener listener = new();

			listener.PeerConnectedEvent += PeerConnectedEventHandler;

			listener.PeerDisconnectedEvent += PeerDisconnectedEventHandler;

			listener.NetworkReceiveEvent += NetworkReceiveEventHandler;

			return StartRelayConnection(listener, RelayAddress, RelayPort, RelayKey, DisconnectTimeoutMilliseconds, Mtu, Transport.DoNotRoute);
		}

		internal bool StopConnection()
		{
			if (SocketState is RelaySocketState.Stopping or RelaySocketState.Stopped) return false;

			SocketState = RelaySocketState.Stopping;

			_virtualClients.Clear();

			_claimToken = string.Empty;

			_claimExistingRoom = false;

			DisposeOutgoingPackets();

			StopSocket(true);

			return true;
		}

		internal void DisconnectRemoteClient(ulong clientId)
		{
			if (SocketState != RelaySocketState.Started || !_virtualClients.Remove(clientId)) return;

			if (RelayPeer is not { ConnectionState: ConnectionState.Connected }) return;

			if (clientId > int.MaxValue) return;

			byte[] kickMessage = MessageCodec.CreateKick((int)clientId);

			RelayPeer.Send(kickMessage, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);
		}

		internal void IterateOutgoing()
		{
			if (SocketState != RelaySocketState.Started || RelayPeer is not { ConnectionState: ConnectionState.Connected })
			{
				DisposeOutgoingPackets();

				return;
			}

			while (OutgoingPackets.TryDequeue(out DataPacket outgoingPacket))
			{
				try
				{
					SendPacket(outgoingPacket);
				}
				finally
				{
					outgoingPacket.Dispose();
				}
			}
		}

		internal void SendToClient(byte channelId, ArraySegment<byte> payload, ulong clientId)
		{
			Send(OutgoingPackets, channelId, payload, clientId);
		}

		private void SendPacket(DataPacket packet)
		{
			if (packet.ClientId > int.MaxValue) return;

			int targetClientId = (int)packet.ClientId;

			byte gameChannel = packet.ChannelId;

			DeliveryMethod deliveryMethod = GetDeliveryMethod(gameChannel);

			ReadOnlySpan<byte> payload = packet.AsSpan();

			int frameSize = MessageCodec.HostDataHeaderSize + payload.Length;

			int maxSinglePacketSize = RelayPeer.GetMaxSinglePacketSize(deliveryMethod);

			if (frameSize > maxSinglePacketSize)
			{
				if (deliveryMethod == DeliveryMethod.Unreliable) return;

				byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameSize);

				try
				{
					int written = MessageCodec.WriteHostData(frameBuffer, targetClientId, gameChannel, payload);

					RelayPeer.Send(frameBuffer, 0, written, MessageCodec.RelayWireChannel, deliveryMethod);
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(frameBuffer);
				}

				return;
			}

			PooledPacket pooledPacket = RelayPeer.CreatePacketFromPool(deliveryMethod, MessageCodec.RelayWireChannel);

			int pooledBytes = MessageCodec.WriteHostData(pooledPacket.Data.AsSpan(pooledPacket.UserDataOffset, frameSize), targetClientId, gameChannel, payload);

			RelayPeer.SendPooledPacket(pooledPacket, pooledBytes);
		}

		private void PeerConnectedEventHandler(NetPeer peer)
		{
			RelayPeer = peer;

			byte[] registerMessage = _claimExistingRoom ? MessageCodec.CreateHostClaim(RoomCode, _claimToken) : MessageCodec.CreateHostRegister(_maximumClients);

			peer.Send(registerMessage, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);
		}

		private void PeerDisconnectedEventHandler(NetPeer peer, DisconnectInfo disconnectInfo)
		{
			RelayPeer = null;

			if (SocketState is RelaySocketState.Stopping or RelaySocketState.Stopped) return;

			Transport.SetRelayDisconnectEvent(NetworkTransport.DisconnectEvents.ClosedByRemote, $"Relay host connection lost. Reason: {disconnectInfo.Reason}.");

			StopSocket(false);

			Transport.EnqueueTransportFailure();
		}

		private void NetworkReceiveEventHandler(NetPeer fromPeer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
		{
			try
			{
				int dataLength = reader.UserDataSize;

				if (dataLength < 1) return;

				ReadOnlySpan<byte> relayPayload = new(reader.RawData, reader.UserDataOffset, dataLength);

				switch (MessageCodec.ReadMessageType(relayPayload))
				{
					case MessageType.RoomCreated:
					{
						MessageCodec.ReadRoomCreated(relayPayload, out string roomCode, out string roomHostToken);

						Transport.SetRoomCode(roomCode);

						Transport.SetRoomHostToken(roomHostToken);

						Transport.HandleRelayHostAvailability(true);

						if (_claimExistingRoom) Transport.ClearPendingHostPromotion();

						SocketState = RelaySocketState.Started;

						break;
					}

					case MessageType.Connected:
					{
						int virtualClientId = MessageCodec.ReadConnected(relayPayload);

						if (virtualClientId < 1) return;

						ulong clientId = (ulong)virtualClientId;

						if (_virtualClients.Count >= _maximumClients)
						{
							byte[] kickMessage = MessageCodec.CreateKick(virtualClientId);

							RelayPeer?.Send(kickMessage, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);

							return;
						}

						if (_virtualClients.Add(clientId)) Transport.EnqueueConnect(clientId);

						break;
					}

					case MessageType.Disconnected:
					{
						int virtualClientId = MessageCodec.ReadDisconnected(relayPayload);

						if (virtualClientId < 1) return;

						ulong clientId = (ulong)virtualClientId;

						if (_virtualClients.Remove(clientId)) Transport.EnqueueDisconnect(clientId);

						break;
					}

					case MessageType.Data:
					{
						MessageCodec.ReadHostData(relayPayload, out int sourceVirtualId, out byte gameChannel, out ReadOnlySpan<byte> gamePayload);

						if (sourceVirtualId < 1) return;

						ulong clientId = (ulong)sourceVirtualId;

						if (!_virtualClients.Contains(clientId)) return;

						if (MessageCodec.IsUnreliableGameChannel(gameChannel) && gamePayload.Length > Mtu) return;

						byte[] payloadData = ArrayPool<byte>.Shared.Rent(gamePayload.Length);

						gamePayload.CopyTo(payloadData.AsSpan(0, gamePayload.Length));

						Transport.EnqueueData(clientId, DataPacket.TakeRentedBuffer(clientId, payloadData, gamePayload.Length, gameChannel));

						break;
					}

					case MessageType.Error:
					{
						ErrorCode errorCode = MessageCodec.ReadError(relayPayload);

						Transport.SetRelayDisconnectEvent(NetworkTransport.DisconnectEvents.ProtocolError, $"Relay returned error code {errorCode}.");

						StopConnection();

						Transport.EnqueueTransportFailure();

						break;
					}
				}
			}
			finally
			{
				reader.Recycle();
			}
		}
	}
}
