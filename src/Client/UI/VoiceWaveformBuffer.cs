using System;

namespace YHPVC.Client;

public sealed class VoiceWaveformBuffer
{
	private readonly object Sync = new();
	private readonly byte[] Levels;

	private int WriteIndex;
	private int Count;
	private long LastUpdateMSUnsafe;

	public VoiceWaveformBuffer(int capacity = 96)
	{
		if (capacity < 8) capacity = 8;
		Levels = new byte[capacity];
	}

	public int Capacity => Levels.Length;

	public long LastUpdateMS { get { lock (Sync) return LastUpdateMSUnsafe; } }

	public void PushLevel(float normalizedLevel)
	{
		byte level = (byte)Math.Clamp((int)(normalizedLevel * 255f), 0, 255);

		lock (Sync)
		{
			Levels[WriteIndex] = level;
			WriteIndex = (WriteIndex + 1) % Levels.Length;
			if (Count < Levels.Length) Count++;
			LastUpdateMSUnsafe = Environment.TickCount64;
		}
	}

	public void CopyLatest(byte[] destination)
	{
		if (destination.Length == 0) return;

		lock (Sync)
		{
			Array.Clear(destination, 0, destination.Length);

			int available = Math.Min(Count, destination.Length);
			int sourceIndex = WriteIndex - available;
			if (sourceIndex < 0) sourceIndex += Levels.Length;

			int destinationIndex = destination.Length - available;

			for (int i = 0; i < available; i++)
			{
				destination[destinationIndex + i] = Levels[sourceIndex];
				sourceIndex++;
				if (sourceIndex == Levels.Length) sourceIndex = 0;
			}
		}
	}

	public void Clear()
	{
		lock (Sync)
		{
			Array.Clear(Levels, 0, Levels.Length);
			WriteIndex = 0;
			Count = 0;
			LastUpdateMSUnsafe = 0;
		}
	}
}
