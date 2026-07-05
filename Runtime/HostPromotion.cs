namespace BlitzRelay.Ngo
{
	public readonly struct HostPromotion
	{
		public readonly string RoomCode;

		public readonly int MaximumClients;

		public HostPromotion(string roomCode, int maximumClients)
		{
			RoomCode = roomCode;

			MaximumClients = maximumClients;
		}
	}
}
