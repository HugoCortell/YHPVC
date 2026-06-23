using System;
using System.Collections.Generic;
using YHPVC.Common;

namespace YHPVC.Client;

internal readonly struct VoiceDecodeStats
{
	public VoiceDecodeStats(int receivedPackets, int lostPackets, int latePackets)
	{
		ReceivedPackets = receivedPackets;
		LostPackets = lostPackets;
		LatePackets = latePackets;
	}

	public int ReceivedPackets { get; }
	public int LostPackets { get; }
	public int LatePackets { get; }
}

internal sealed class SpeakerJitterBuffer : IDisposable
{
	private readonly Dictionary<ushort, ServerVoiceFrame> Frames = new(16);
	private readonly List<ushort> RemoveScratch = new(16);
	private readonly OpusDecoderAdapter Decoder;
	private readonly int JitterTargetMS;
	private readonly int JitterMaxMS;
	private readonly int FrameDurationMS;

	private bool PlaybackStarted;
	private ushort ExpectedSequence;
	private long NextDecodeDueMs;
	private ushort NewestSequence;
	private bool HaveNewestSequence;

	private int ReceivedPackets;
	private int LostPackets;
	private int LatePackets;

	public ushort SpeakerPeerID { get; }
	public long LastReceivedMS { get; private set; }

	public SpeakerJitterBuffer(ushort speakerPeerId, int sampleRate, int frameDurationMs, int jitterTargetMs, int jitterMaxMs)
	{
		SpeakerPeerID = speakerPeerId;
		Decoder = new OpusDecoderAdapter(sampleRate, frameDurationMs);
		this.FrameDurationMS = frameDurationMs;
		this.JitterTargetMS = jitterTargetMs;
		this.JitterMaxMS = jitterMaxMs;
	}

	public void Enqueue(ServerVoiceFrame frame, long receivedMs)
	{
		LastReceivedMS = receivedMs;
		ReceivedPackets++;

		if (!HaveNewestSequence)
		{
			NewestSequence = frame.Sequence;
			HaveNewestSequence = true;
		}
		else if (VoiceMath.SequenceNewerThan(frame.Sequence, NewestSequence)) { NewestSequence = frame.Sequence; }

		if (PlaybackStarted && VoiceMath.SequenceOlderThan(frame.Sequence, ExpectedSequence)) { LatePackets++; return; }
		if (!Frames.ContainsKey(frame.Sequence)) { Frames[frame.Sequence] = frame; }

		TrimExcessBufferedFrames(receivedMs);
	}

	public bool TryDecodeDue(long nowMs, out ReadOnlySpan<short> pcm)
	{
		pcm = default;

		if (!EnsurePlaybackStarted(nowMs)) return false;
		if (nowMs < NextDecodeDueMs) return false;

		ushort currentSeq = ExpectedSequence;
		ushort nextSeq = VoiceMath.SequenceAdd(currentSeq, 1);

		if (Frames.Remove(currentSeq, out var frame)) { pcm = Decoder.DecodeToScratch(frame.Opus, decodeFec: false); }
		else if (Frames.TryGetValue(nextSeq, out var nextFrame) && (nextFrame.Flags & YHPVCConstants.FlagFecCapable) != 0)
		{
			// Opus in-band FEC for packet N is carried inside packet N+1. Decode it now, leave N+1 buffered, and decode N+1 normally on the next tick.
			LostPackets++;
			pcm = Decoder.DecodeToScratch(nextFrame.Opus, decodeFec: true);
		}
		else
		{
			if (Frames.Count == 0 && nowMs - LastReceivedMS > JitterMaxMS)
			{
				PlaybackStarted = false;
				return false;
			}

			LostPackets++;
			pcm = Decoder.DecodePlcToScratch();
		}

		ExpectedSequence = nextSeq;
		NextDecodeDueMs += FrameDurationMS;

		if (nowMs - NextDecodeDueMs > JitterMaxMS)
		{
			// If the client stalls, do not attempt to catch up by decoding a long burst. Resume near the present so audio latency remains bounded.
			NextDecodeDueMs = nowMs + FrameDurationMS;
		}

		TrimOldSequences();
		return true;
	}

	public void SkipDue(long nowMs)
	{
		if (!EnsurePlaybackStarted(nowMs)) return;
		if (nowMs < NextDecodeDueMs) return;

		int maxSkips = Math.Max(1, (JitterMaxMS / Math.Max(1, FrameDurationMS)) + 2);
		int skipped = 0;

		while (nowMs >= NextDecodeDueMs && skipped++ < maxSkips)
		{
			ushort currentSeq = ExpectedSequence;
			Frames.Remove(currentSeq);

			ExpectedSequence = VoiceMath.SequenceAdd(currentSeq, 1);
			NextDecodeDueMs += FrameDurationMS;
		}

		if (nowMs - NextDecodeDueMs > JitterMaxMS) { NextDecodeDueMs = nowMs + FrameDurationMS; }
		if (Frames.Count == 0 && nowMs - LastReceivedMS > JitterMaxMS) { PlaybackStarted = false; return; }

		TrimOldSequences();
	}

	public VoiceDecodeStats TakeStats()
	{
		var stats = new VoiceDecodeStats(ReceivedPackets, LostPackets, LatePackets);
		ReceivedPackets = 0;
		LostPackets = 0;
		LatePackets = 0;
		return stats;
	}

	private bool EnsurePlaybackStarted(long nowMs)
	{
		if (PlaybackStarted) return true;
		if (Frames.Count == 0) return false;

		ushort first = FindOldestSequence();
		ServerVoiceFrame firstFrame = Frames[first];
		if (nowMs - LastReceivedMS < JitterTargetMS && Frames.Count < Math.Max(2, JitterTargetMS / Math.Max(1, FrameDurationMS))) return false;

		PlaybackStarted = true;
		ExpectedSequence = firstFrame.Sequence;
		NextDecodeDueMs = nowMs;
		return true;
	}

	private ushort FindOldestSequence()
	{
		using var enumerator = Frames.Keys.GetEnumerator();
		if (!enumerator.MoveNext()) return 0;

		ushort oldest = enumerator.Current;
		while (enumerator.MoveNext())
		{
			ushort candidate = enumerator.Current;
			if (VoiceMath.SequenceOlderThan(candidate, oldest)) oldest = candidate;
		}

		return oldest;
	}

	private void TrimExcessBufferedFrames(long nowMs)
	{
		int maxBuffered = Math.Max(4, (JitterMaxMS / Math.Max(1, FrameDurationMS)) + 4);
		if (Frames.Count <= maxBuffered) return;

		// Drop the oldest not-yet-played packets first. This should only happen if the receiver is severely behind or someone sends malformed bursts.
		while (Frames.Count > maxBuffered)
		{
			ushort oldest = FindOldestSequence();
			Frames.Remove(oldest);
			LatePackets++;
		}

		if (PlaybackStarted && nowMs - NextDecodeDueMs > JitterMaxMS) { NextDecodeDueMs = nowMs; }
	}

	private void TrimOldSequences()
	{
		if (Frames.Count == 0 || !PlaybackStarted) return;

		RemoveScratch.Clear();
		foreach (ushort seq in Frames.Keys) { if (VoiceMath.SequenceOlderThan(seq, ExpectedSequence)) RemoveScratch.Add(seq); }

		for (int i = 0; i < RemoveScratch.Count; i++) Frames.Remove(RemoveScratch[i]);
		RemoveScratch.Clear();
	}

	public void Dispose() => Decoder.Dispose();
}
