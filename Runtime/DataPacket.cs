using BlitzRelay.Protocol;
using JetBrains.Annotations;
using System;
using System.Buffers;

namespace BlitzRelay.Ngo
{
	public readonly struct DataPacket : IDisposable
	{
		public readonly ulong ClientId;

		public readonly byte ChannelId;

		private readonly bool _isPooled;

		[NotNull]
		private readonly byte[] _data;

		private readonly int _length;

		private DataPacket(ulong clientId, bool isPooled, [NotNull] byte[] data, int length, byte channelId)
		{
			ClientId = clientId;

			_isPooled = isPooled;

			_data = data;

			_length = length;

			ChannelId = channelId;
		}

		public DataPacket(ulong clientId, ArraySegment<byte> segment, byte channelId)
		{
			ClientId = clientId;

			ChannelId = channelId;

			if (segment.Count == 0)
			{
				_isPooled = false;

				_data = Array.Empty<byte>();

				_length = 0;

				return;
			}

			if (segment.Array == null) throw new ArgumentException("Payload array is null.", nameof(segment));

			_isPooled = true;

			_data = ArrayPool<byte>.Shared.Rent(segment.Count);

			Buffer.BlockCopy(segment.Array, segment.Offset, _data, 0, segment.Count);

			_length = segment.Count;
		}

		public static DataPacket TakeRentedBuffer(ulong clientId, [NotNull] byte[] data, int length, byte channelId = (byte)GameChannel.Reliable)
		{
			return new DataPacket(clientId, true, data, length, channelId);
		}

		public ArraySegment<byte> ToArraySegment()
		{
			return new ArraySegment<byte>(_data, 0, _length);
		}

		public ReadOnlySpan<byte> AsSpan()
		{
			return _data.AsSpan(0, _length);
		}

		public void Dispose()
		{
			if (_isPooled) ArrayPool<byte>.Shared.Return(_data);
		}
	}
}
