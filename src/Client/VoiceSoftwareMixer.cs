using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using YHPVC.Common;

namespace YHPVC.Client;

internal readonly struct VoiceDebugSpeakerLine
{
	public VoiceDebugSpeakerLine
	(
		double speakerX,
		double speakerY,
		double speakerZ,
		double listenerX,
		double listenerY,
		double listenerZ,
		int dimension,
		float gain
	)
	{
		SpeakerX = speakerX;
		SpeakerY = speakerY;
		SpeakerZ = speakerZ;
		ListenerX = listenerX;
		ListenerY = listenerY;
		ListenerZ = listenerZ;
		Dimension = dimension;
		Gain = gain;
	}

	public double SpeakerX { get; }
	public double SpeakerY { get; }
	public double SpeakerZ { get; }

	public double ListenerX { get; }
	public double ListenerY { get; }
	public double ListenerZ { get; }

	public int Dimension { get; }
	public float Gain { get; }
}

internal sealed class VoiceSoftwareMixer : IDisposable
{
	private readonly ICoreClientAPI ClientAPI;
	private readonly YHPVCClientConfig ClientConfig;
	private readonly VoicePeerTable PeerTable;
	private readonly Dictionary<ushort, SpeakerJitterBuffer> Buffers = new();
	private readonly List<ushort> RemoveScratch = new(16);
	private readonly IVoicePostProcessor PostProcessor = new DefaultVoicePostProcessor();
	private readonly VoiceIncomingEffectsProcessor IncomingProcessor = new();

	private VoiceIncomingEffectState incomingEffects = VoiceIncomingEffectState.Disabled;
	private int SampleRate = 16000;
	private int FrameDurationMS = 20;
	private int FrameSamples = 320;
	private double AudibleRangeBlocks = 48;
	private float FullVolumeDistanceBlocks = 2f;
	private float[] MixStereo = new float[640];
	private const int MaxLocalMonitorFrames = 8;

	private readonly Queue<short[]> LocalMonitorFrames = new(MaxLocalMonitorFrames);
	private readonly Queue<short[]> LocalMonitorPool = new(MaxLocalMonitorFrames);

	public VoiceSoftwareMixer(ICoreClientAPI api, YHPVCClientConfig clientCfg, VoicePeerTable peerTable)
	{
		this.ClientAPI = api;
		this.ClientConfig = clientCfg;
		this.PeerTable = peerTable;
	}

	public int OutputSampleCount => FrameSamples * 2;

	public void Configure(ServerHelloMsg hello)
	{
		SampleRate = hello.SampleRate;
		FrameDurationMS = hello.FrameDurationMS;
		FrameSamples = VoiceMath.FrameSamples(SampleRate, FrameDurationMS);
		AudibleRangeBlocks = Math.Max(0.001, hello.AudibleRangeBlocks);
		FullVolumeDistanceBlocks = Math.Clamp(hello.FullVolumeDistanceBlocks, 0f, Math.Max(0f, (float)AudibleRangeBlocks - 0.01f));
		MixStereo = new float[FrameSamples * 2];
		IncomingProcessor.Configure(SampleRate);
	}

	public void SetIncomingEffects(VoiceIncomingEffectState state) { incomingEffects = state; }

	public void RemovePeer(ushort peerId)
	{
		if (!Buffers.Remove(peerId, out var buffer)) return;
		buffer.Dispose();
	}

	public void Enqueue(ServerVoiceFrame frame, long receivedMs)
	{
		if (frame.SpeakerPeerID == PeerTable.LocalPeerID) return;

		if (!Buffers.TryGetValue(frame.SpeakerPeerID, out var buffer))
		{
			buffer = new SpeakerJitterBuffer(frame.SpeakerPeerID, SampleRate, FrameDurationMS, ClientConfig.JitterTargetMS, ClientConfig.JitterMaxMS);
			Buffers[frame.SpeakerPeerID] = buffer;
		}

		buffer.Enqueue(frame, receivedMs);
	}

	public void EnqueueLocalMonitorFrame(ReadOnlySpan<short> pcm)
	{
		if (!ClientConfig.HearSelf || pcm.Length == 0 || FrameSamples <= 0) return;

		short[] frame = LocalMonitorPool.Count > 0 ? LocalMonitorPool.Dequeue() : new short[FrameSamples];
		if (frame.Length != FrameSamples) frame = new short[FrameSamples];

		int copy = Math.Min(FrameSamples, pcm.Length);
		pcm[..copy].CopyTo(frame);

		if (copy < frame.Length) { Array.Clear(frame, copy, frame.Length - copy); }

		LocalMonitorFrames.Enqueue(frame);
		while (LocalMonitorFrames.Count > MaxLocalMonitorFrames) { ReturnLocalMonitorFrame(LocalMonitorFrames.Dequeue()); }
	}

	public void MixNextFrameInto(Span<short> output, long playbackNowMs)
	{
		if (output.Length == 0) return;

		bool hasRemoteBuffers = Buffers.Count > 0;
		bool hasLocalMonitor = LocalMonitorFrames.Count > 0;
		if (!hasRemoteBuffers && !hasLocalMonitor) { ClearSilentOutput(output); return; }

		Array.Clear(MixStereo, 0, MixStereo.Length);

		bool mixedRemote = false;
		long now = playbackNowMs;

		RemoveScratch.Clear();

		foreach (var kvp in Buffers)
		{
			ushort peerId = kvp.Key;
			SpeakerJitterBuffer buffer = kvp.Value;

			if (now - buffer.LastReceivedMS > 3000) { RemoveScratch.Add(peerId); continue; }
			if (!TryGetSpatialContext(peerId, out var ctx) || ctx.Gain <= 0f) { buffer.SkipDue(now); continue; }
			if (!buffer.TryDecodeDue(now, out var pcm)) continue;

			PostProcessor.ProcessSpeakerFrame(peerId, pcm, MixStereo, ctx);
			mixedRemote = true;
		}

		if (RemoveScratch.Count > 0)
		{
			for (int i = 0; i < RemoveScratch.Count; i++) RemovePeer(RemoveScratch[i]);
			RemoveScratch.Clear();
		}

		float master = ClientConfig.IncomingGain;
		int sampleCount = Math.Min(output.Length, MixStereo.Length);

		if (mixedRemote) { ApplyIncomingEffects(MixStereo.AsSpan(0, sampleCount)); }
		else { ResetIncomingEffectsForSilence(); }

		bool mixedLocalMonitor = TryAddLocalMonitorToMix(sampleCount);

		if (!mixedRemote && !mixedLocalMonitor) { ClearSilentOutput(output); return; }
		for (int i = 0; i < sampleCount; i++) { output[i] = VoiceMath.FloatToPcm16(VoiceMath.SoftLimit(MixStereo[i] * master)); }

		output[sampleCount..].Clear();
	}

	private bool TryAddLocalMonitorToMix(int outputSampleCount)
	{
		if (LocalMonitorFrames.Count == 0) return false;

		short[] frame = LocalMonitorFrames.Dequeue();
		try
		{
			int monoSamples = Math.Min(frame.Length, outputSampleCount / 2);
			if (monoSamples <= 0) return false;

			const float centerGain = 0.70710677f;
			for (int i = 0; i < monoSamples; i++)
			{
				float sample = (frame[i] / 32768f) * centerGain;
				int o = i * 2;
				MixStereo[o] += sample;
				MixStereo[o + 1] += sample;
			}

			return true;
		}
		finally { ReturnLocalMonitorFrame(frame); }
	}

	private void ReturnLocalMonitorFrame(short[] frame) { if (LocalMonitorPool.Count < MaxLocalMonitorFrames) { LocalMonitorPool.Enqueue(frame); } }

	private void ApplyIncomingEffects(Span<float> finalRemoteMix) { IncomingProcessor.Process(finalRemoteMix, incomingEffects); }
	
	private void ClearSilentOutput(Span<short> output) { ResetIncomingEffectsForSilence(); output.Clear(); }

	private void ResetIncomingEffectsForSilence() { IncomingProcessor.ResetForSilence(incomingEffects); }

	public void CollectDebugSpeakerLines(List<VoiceDebugSpeakerLine> into, long nowMs, int activeMs)
	{
		IClientPlayer? listener = ClientAPI.World.Player;
		var lp = listener?.Entity?.Pos;
		if (lp == null) return;

		double lx = lp.X;
		double ly = lp.Y + lp.DimensionYAdjustment;
		double lz = lp.Z;

		foreach (var kvp in Buffers)
		{
			ushort peerId = kvp.Key;
			SpeakerJitterBuffer buffer = kvp.Value;

			if (nowMs - buffer.LastReceivedMS > activeMs) continue;
			if (!PeerTable.TryGet(peerId, out var peer)) continue;

			IPlayer? speaker = ClientAPI.World.PlayerByUid(peer.PlayerUID);
			var sp = speaker?.Entity?.Pos;
			if (sp == null) continue;
			if (sp.Dimension != lp.Dimension) continue;

			double sx = sp.X;
			double sy = sp.Y + sp.DimensionYAdjustment;
			double sz = sp.Z;

			double dx = sx - lx;
			double dy = sy - ly;
			double dz = sz - lz;
			double distSq = dx * dx + dy * dy + dz * dz;

			float gain = VoiceMath.DistanceGainWithFullVolumeZone(distSq, FullVolumeDistanceBlocks, AudibleRangeBlocks);
			into.Add(new VoiceDebugSpeakerLine(sx, sy, sz, lx, ly, lz, lp.Dimension, gain));
		}
	}

	public VoiceDecodeStats TakeStats()
	{
		int received = 0;
		int lost = 0;
		int late = 0;

		foreach (SpeakerJitterBuffer buffer in Buffers.Values)
		{
			VoiceDecodeStats stats = buffer.TakeStats();
			received += stats.ReceivedPackets;
			lost += stats.LostPackets;
			late += stats.LatePackets;
		}

		return new VoiceDecodeStats(received, lost, late);
	}

	private bool TryGetSpatialContext(ushort peerId, out VoiceSpatialContext context)
	{
		context = default;

		IClientPlayer? listener = ClientAPI.World.Player;
		IPlayer? speaker;

		if (peerId == PeerTable.LocalPeerID) { speaker = listener; }
		else
		{
			if (!PeerTable.TryGet(peerId, out var peer)) return false;
			speaker = ClientAPI.World.PlayerByUid(peer.PlayerUID);
		}

		var sp = speaker?.Entity?.Pos;
		var lp = listener?.Entity?.Pos;
		if (sp == null || lp == null) return false;
		if (sp.Dimension != lp.Dimension) return false;

		double dx = sp.X - lp.X;
		double dy = (sp.Y + sp.DimensionYAdjustment) - (lp.Y + lp.DimensionYAdjustment);
		double dz = sp.Z - lp.Z;
		double distSq = dx * dx + dy * dy + dz * dz;

		float distanceGain = VoiceMath.DistanceGainWithFullVolumeZone(distSq, FullVolumeDistanceBlocks, AudibleRangeBlocks);
		if (distanceGain <= 0f)
		{
			context = new VoiceSpatialContext(peerId, 0f, 0f, 0f, 0f, distSq);
			return true;
		}

		float pan = VoiceSpatialMath.ComputePan(dx, dz, lp.Yaw) * ClientConfig.StereoPanStrength;
		if (ClientConfig.InvertStereoPan) pan = -pan;

		VoiceMath.EqualPowerPan(pan, out float leftGain, out float rightGain);
		context = new VoiceSpatialContext(peerId, distanceGain, leftGain, rightGain, pan, distSq);
		return true;
	}

	public void Dispose()
	{
		foreach (var buffer in Buffers.Values) buffer.Dispose();
		Buffers.Clear();
		RemoveScratch.Clear();
		LocalMonitorFrames.Clear();
		LocalMonitorPool.Clear();
		IncomingProcessor.Reset();
	}
}
