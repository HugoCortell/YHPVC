using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;

namespace YHPVC.Client;

internal sealed class PooledPcmFrame
{
	public PooledPcmFrame(int sampleCount)
	{
		Buffer = new short[sampleCount];
		SampleCount = sampleCount;
	}

	public short[] Buffer { get; private set; }
	public int SampleCount { get; set; }

	public void EnsureCapacity(int sampleCount)
	{
		if (Buffer.Length < sampleCount) { Buffer = new short[sampleCount]; }
		SampleCount = sampleCount;
	}
}

internal sealed class OpenAlStreamingOutput : IDisposable
{
	private const int BufferCount = 6;
	private const int StartPlaybackBufferCount = 3;
	private const int MaxPendingChunks = 24;
	private const int MaxPooledFrames = BufferCount + MaxPendingChunks + 8;

	private readonly ICoreClientAPI ClientAPI;
	private readonly ConcurrentQueue<PooledPcmFrame> PendingPCM = new();
	private readonly Queue<int> FreeBuffers = new(BufferCount);
	private readonly Queue<PooledPcmFrame> FramePool = new(MaxPooledFrames);
	private readonly object FramePoolSync = new();

	private readonly int[] Buffers = new int[BufferCount];
	private int Source;
	private long TickID;
	private int SampleRate = 16000;
	private int Channels = 2;
	private int PendingCount;
	private volatile bool Started;
	private volatile bool Disposed;

	public OpenAlStreamingOutput(ICoreClientAPI api) { this.ClientAPI = api; }

	public void Start(int sampleRate, int channels)
	{
		this.SampleRate = sampleRate;
		this.Channels = channels == 1 ? 1 : 2;

		ClientAPI.Event.EnqueueMainThreadTask(() =>
		{
			if (Disposed || Started) return;

			try
			{
				FreeBuffers.Clear();
				for (int i = 0; i < Buffers.Length; i++)
				{
					Buffers[i] = AL.GenBuffer();
					FreeBuffers.Enqueue(Buffers[i]);
				}

				Source = AL.GenSource();
				AL.Source(Source, ALSourceb.SourceRelative, true);
				AL.Source(Source, ALSourcef.Gain, 1f);
				AL.Source(Source, ALSource3f.Position, 0f, 0f, 0f);

				TickID = ClientAPI.Event.RegisterGameTickListener(_ => TickMainThread(), 1);
				Started = true;
			}
			catch (Exception ex)
			{
				ClientAPI.Logger.Warning("[YHPVC] Failed to start OpenAL output: {0}", ex);
				StopMainThread();
			}
		}, "yhpvc-start-output");
	}

	public PooledPcmFrame RentFrame(int sampleCount)
	{
		lock (FramePoolSync)
		{
			if (FramePool.Count > 0)
			{
				PooledPcmFrame frame = FramePool.Dequeue();
				frame.EnsureCapacity(sampleCount);
				return frame;
			}
		}

		return new PooledPcmFrame(sampleCount);
	}

	public void Queue(PooledPcmFrame frame)
	{
		if (Disposed || frame.SampleCount == 0)
		{
			ReturnFrame(frame);
			return;
		}

		PendingPCM.Enqueue(frame);
		Interlocked.Increment(ref PendingCount);

		while (Volatile.Read(ref PendingCount) > MaxPendingChunks && PendingPCM.TryDequeue(out var dropped))
		{
			Interlocked.Decrement(ref PendingCount);
			ReturnFrame(dropped);
		}
	}

	private bool TryDequeue(out PooledPcmFrame frame)
	{
		if (PendingPCM.TryDequeue(out frame!))
		{
			Interlocked.Decrement(ref PendingCount);
			return true;
		}

		return false;
	}

	private void ReturnFrame(PooledPcmFrame frame)
	{
		frame.SampleCount = 0;
		lock (FramePoolSync) { if (FramePool.Count < MaxPooledFrames) { FramePool.Enqueue(frame); } }
	}

	private void TickMainThread()
	{
		if (Source == 0) return;

		try
		{
			AL.GetSource(Source, ALGetSourcei.BuffersProcessed, out int processed);
			while (processed-- > 0)
			{
				int buffer = AL.SourceUnqueueBuffer(Source);
				FreeBuffers.Enqueue(buffer);
			}

			while (FreeBuffers.Count > 0 && TryDequeue(out var frame))
			{
				int buffer = FreeBuffers.Dequeue();
				try
				{
					FillBuffer(buffer, frame);
					AL.SourceQueueBuffer(Source, buffer);
				}
				finally { ReturnFrame(frame); }
			}

			AL.GetSource(Source, ALGetSourcei.BuffersQueued, out int queued);
			AL.GetSource(Source, ALGetSourcei.SourceState, out int stateInt);
			if ((ALSourceState)stateInt != ALSourceState.Playing && queued >= StartPlaybackBufferCount) { AL.SourcePlay(Source); }
		}
		catch (Exception ex)
		{
			ClientAPI.Logger.Warning("[YHPVC] OpenAL output tick failed: {0}", ex);
			StopMainThread();
		}
	}

	private void FillBuffer(int buffer, PooledPcmFrame frame)
	{
		var handle = GCHandle.Alloc(frame.Buffer, GCHandleType.Pinned);
		try
		{
			AL.BufferData(buffer, Channels == 1 ? ALFormat.Mono16 : ALFormat.Stereo16, handle.AddrOfPinnedObject(), frame.SampleCount * sizeof(short), SampleRate);
		}
		finally { handle.Free(); }
	}


	public void Stop() { ClientAPI.Event.EnqueueMainThreadTask(StopMainThread, "yhpvc-stop-output"); }

	private void StopMainThread()
	{
		try
		{
			if (TickID != 0)
			{
				ClientAPI.Event.UnregisterGameTickListener(TickID);
				TickID = 0;
			}
		}
		catch { }

		try { if (Source != 0) AL.SourceStop(Source); }
		catch { }

		try
		{
			if (Source != 0)
			{
				AL.GetSource(Source, ALGetSourcei.BuffersQueued, out int queued);
				while (queued-- > 0)
				{
					try { AL.SourceUnqueueBuffer(Source); }
					catch { break; }
				}
			}
		}
		catch { }
		FreeBuffers.Clear();

		try
		{
			for (int i = 0; i < Buffers.Length; i++)
			{
				if (Buffers[i] != 0) AL.DeleteBuffer(Buffers[i]);
				Buffers[i] = 0;
			}
		}
		catch { }

		try { if (Source != 0) AL.DeleteSource(Source); }
		catch { }


		Source = 0;
		Started = false;

		while (PendingPCM.TryDequeue(out var frame)) { ReturnFrame(frame); }

		Interlocked.Exchange(ref PendingCount, 0);
	}

	public void Dispose()
	{
		Disposed = true;
		Stop();
	}
}
