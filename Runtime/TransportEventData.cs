using Unity.Netcode;

namespace BlitzRelay.Ngo
{
	public readonly struct TransportEventData
	{
		public readonly NetworkEvent EventType;

		public readonly ulong ClientId;

		public readonly DataPacket? Packet;

		public readonly float ReceiveTime;

		private TransportEventData(NetworkEvent eventType, ulong clientId, DataPacket? packet, float receiveTime)
		{
			EventType = eventType;

			ClientId = clientId;

			Packet = packet;

			ReceiveTime = receiveTime;
		}

		public static TransportEventData Create(NetworkEvent eventType, ulong clientId, float receiveTime)
		{
			return new TransportEventData(eventType, clientId, null, receiveTime);
		}

		public static TransportEventData CreateData(ulong clientId, DataPacket packet, float receiveTime)
		{
			return new TransportEventData(NetworkEvent.Data, clientId, packet, receiveTime);
		}

		public void Dispose()
		{
			Packet?.Dispose();
		}
	}
}
