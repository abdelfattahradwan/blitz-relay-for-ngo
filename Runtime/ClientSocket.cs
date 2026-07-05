using BlitzRelay.Ngo.LiteNetLib;
using BlitzRelay.Protocol;
using System;
using System.Buffers;
using Unity.Netcode;

namespace BlitzRelay.Ngo
{
	public sealed class ClientSocket : SocketBase
	{
		private bool _pendingPromotionDisconnect;

		internal bool StartConnection(string relayAddress, ushort relayPort, string relayKey, string roomCode)
		{
			if (SocketState != RelaySocketState.Stopped) StopConnection(false);

			SocketState = RelaySocketState.Starting;

			RelayAddress = relayAddress;

			RelayPort = relayPort;

			RelayKey = relayKey ?? string.Empty;

			RoomCode = roomCode ?? string.Empty;

			_pendingPromotionDisconnect = false;

			DisposeOutgoingPackets();

			EventBasedNetListener listener = new();

			listener.PeerConnectedEvent += PeerConnectedEventHandler;

			listener.PeerDisconnectedEvent += PeerDisconnectedEventHandler;

			listener.NetworkReceiveEvent += NetworkReceiveEventHandler;

			return StartRelayConnection(listener, RelayAddress, RelayPort, RelayKey, DisconnectTimeoutMilliseconds, Mtu, Transport.DoNotRoute);
		}

		internal bool StopConnection(bool notifyTransport)
		{
			if (SocketState is RelaySocketState.Stopping or RelaySocketState.Stopped) return false;

			SocketState = RelaySocketState.Stopping;

			_pendingPromotionDisconnect = false;

			DisposeOutgoingPackets();

			StopSocket(true);

			if (notifyTransport) Transport.EnqueueDisconnect(Transport.ServerClientId);

			return true;
		}

		internal void IterateOutgoing()
		{
			if (RelayPeer is not { ConnectionState: ConnectionState.Connected })
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

		internal void SendToServer(byte channelId, ArraySegment<byte> payload)
		{
			Send(OutgoingPackets, channelId, payload, Transport.ServerClientId);
		}

		private void SendPacket(DataPacket packet)
		{
			byte gameChannel = packet.ChannelId;

			DeliveryMethod deliveryMethod = GetDeliveryMethod(gameChannel);

			ReadOnlySpan<byte> payload = packet.AsSpan();

			int frameSize = MessageCodec.ClientDataHeaderSize + payload.Length;

			int maxSinglePacketSize = RelayPeer.GetMaxSinglePacketSize(deliveryMethod);

			if (frameSize > maxSinglePacketSize)
			{
				if (deliveryMethod == DeliveryMethod.Unreliable) return;

				byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameSize);

				try
				{
					int written = MessageCodec.WriteClientData(frameBuffer, gameChannel, payload);

					RelayPeer.Send(frameBuffer, 0, written, MessageCodec.RelayWireChannel, deliveryMethod);
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(frameBuffer);
				}

				return;
			}

			PooledPacket pooledPacket = RelayPeer.CreatePacketFromPool(deliveryMethod, MessageCodec.RelayWireChannel);

			int pooledBytes = MessageCodec.WriteClientData(pooledPacket.Data.AsSpan(pooledPacket.UserDataOffset, frameSize), gameChannel, payload);

			RelayPeer.SendPooledPacket(pooledPacket, pooledBytes);
		}

		private void PeerConnectedEventHandler(NetPeer peer)
		{
			RelayPeer = peer;

			byte[] joinMessage = MessageCodec.CreateClientJoin(RoomCode);

			peer.Send(joinMessage, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);
		}

		private void PeerDisconnectedEventHandler(NetPeer peer, DisconnectInfo disconnectInfo)
		{
			RelayPeer = null;

			if (SocketState is RelaySocketState.Stopping or RelaySocketState.Stopped) return;

			NetworkTransport.DisconnectEvents disconnectEvent = _pendingPromotionDisconnect
																	? NetworkTransport.DisconnectEvents.Disconnected
																	: NetworkTransport.DisconnectEvents.ClosedByRemote;

			Transport.SetRelayDisconnectEvent(disconnectEvent, $"Relay client connection closed. Reason: {disconnectInfo.Reason}.");

			StopConnection(true);
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
					case MessageType.JoinSuccess:
					{
						Transport.HandleRelayHostAvailability(true);

						SocketState = RelaySocketState.Started;

						Transport.EnqueueConnect(Transport.ServerClientId);

						break;
					}

					case MessageType.HostPromoted:
					{
						MessageCodec.ReadHostPromoted(relayPayload, out string roomCode, out int maximumClients, out string claimToken);

						Transport.HandleClientHostPromoted(roomCode, maximumClients, claimToken);

						_pendingPromotionDisconnect = true;

						byte[] acknowledgement = MessageCodec.CreateHostPromotionAck(roomCode, claimToken);

						fromPeer.Send(acknowledgement, MessageCodec.RelayWireChannel, DeliveryMethod.ReliableOrdered);

						break;
					}

					case MessageType.HostUnavailable:
					{
						Transport.HandleRelayHostAvailability(false);

						break;
					}

					case MessageType.HostAvailable:
					{
						Transport.HandleRelayHostAvailability(true);

						break;
					}

					case MessageType.Data:
					{
						MessageCodec.ReadClientData(relayPayload, out byte gameChannel, out ReadOnlySpan<byte> gamePayload);

						if (MessageCodec.IsUnreliableGameChannel(gameChannel) && gamePayload.Length > Mtu) return;

						byte[] payloadData = ArrayPool<byte>.Shared.Rent(gamePayload.Length);

						gamePayload.CopyTo(payloadData.AsSpan(0, gamePayload.Length));

						Transport.EnqueueData(Transport.ServerClientId, DataPacket.TakeRentedBuffer(Transport.ServerClientId, payloadData, gamePayload.Length, gameChannel));

						break;
					}

					case MessageType.Disconnected:
					{
						_pendingPromotionDisconnect = false;

						StopConnection(true);

						break;
					}

					case MessageType.Error:
					{
						ErrorCode errorCode = MessageCodec.ReadError(relayPayload);

						_pendingPromotionDisconnect = false;

						Transport.SetRelayDisconnectEvent(NetworkTransport.DisconnectEvents.ProtocolError, $"Relay returned error code {errorCode}.");

						StopConnection(false);

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
