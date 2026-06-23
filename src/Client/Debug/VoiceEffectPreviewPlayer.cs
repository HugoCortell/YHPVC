using System;
using System.IO;
using System.Text;
using Vintagestory.API.Client;
using YHPVC.Client;
using YHPVC.Common;

namespace YHPVC.Client.Debug;

internal readonly struct VoiceEffectPreviewSourceLocation
{
	public VoiceEffectPreviewSourceLocation(double x, double y, double z, int dimension)
	{
		X = x;
		Y = y;
		Z = z;
		Dimension = dimension;
	}

	public double X { get; }
	public double Y { get; }
	public double Z { get; }
	public int Dimension { get; }
}

internal readonly struct VoiceEffectPreviewSettings
{
	public VoiceEffectPreviewSettings
	(
		VoiceEffectPreviewSourceLocation source,
		float drunkIntoxication,
		float psychedelic,
		float caveReverbness,
		bool sourceSubmerged,
		bool listenerSubmerged,
		float temporalGlitchStrength
	)
	{
		Source = source;
		DrunkIntoxication = Math.Clamp(drunkIntoxication, 0f, VoiceImmersiveEffectMapping.MaxGameplayIntoxication);
		Psychedelic = Math.Clamp(psychedelic, 0f, VoiceImmersiveEffectMapping.MaxGameplayPsychedelic);
		CaveReverbness = Math.Clamp(caveReverbness, 0f, VoiceImmersiveEffectMapping.MaxPreviewCaveReverbness);
		SourceSubmerged = sourceSubmerged;
		ListenerSubmerged = listenerSubmerged;
		TemporalGlitchStrength = Math.Clamp(temporalGlitchStrength, 0f, 1f);
	}

	public VoiceEffectPreviewSourceLocation Source { get; }
	public float DrunkIntoxication { get; }
	public float Psychedelic { get; }
	public float CaveReverbness { get; }
	public bool SourceSubmerged { get; }
	public bool ListenerSubmerged { get; }
	public float TemporalGlitchStrength { get; }

	public VoiceOutgoingEffectState BuildOutgoingEffects()
	{
		return new VoiceOutgoingEffectState(
			enabled: true,
			underwaterMuffle: VoiceImmersiveEffectMapping.UnderwaterMuffleFromSubmerged(SourceSubmerged),
			caveReverb: VoiceImmersiveEffectMapping.CaveReverbFromReverbness(CaveReverbness, SourceSubmerged),
			drunkWarp: VoiceImmersiveEffectMapping.DrunkWarpFromIntoxication(DrunkIntoxication),
			psychedelicWarp: VoiceImmersiveEffectMapping.PsychedelicWarpFromPsychedelic(Psychedelic));
	}

	public VoiceIncomingEffectState BuildIncomingEffects()
	{
		return new VoiceIncomingEffectState(
			enabled: true,
			underwaterMuffle: VoiceImmersiveEffectMapping.UnderwaterMuffleFromSubmerged(ListenerSubmerged),
			temporalWarp: VoiceImmersiveEffectMapping.TemporalWarpFromGlitchStrength(TemporalGlitchStrength));
	}
}

internal sealed class VoiceEffectPreviewPlayer : IDisposable
{
	private const ushort DebugSpeakerPeerId = ushort.MaxValue;
	private const int ReverbTailMs = 300;

	private readonly ICoreClientAPI ClientAPI;
	private readonly YHPVCClientConfig ClientConfig;
	private readonly Func<ServerHelloMsg?> GetHelloFunct;
	private readonly DefaultVoicePostProcessor PostProcessor = new();
	private readonly VoiceOutgoingEffectsProcessor OutgoingProcessor = new();
	private readonly VoiceIncomingEffectsProcessor IncomingProcessor = new();

	private OpenAlStreamingOutput? output;
	private long TickID;
	private bool Disposed;

	private VoiceEffectPreviewSettings Settings;
	private short[] ClipMono = Array.Empty<short>();
	private short[] MonoFrame = Array.Empty<short>();
	private float[] MonoScratch = Array.Empty<float>();
	private float[] StereoMix = Array.Empty<float>();

	private int ClipOffset;
	private int TailFramesRemaining;
	private int SampleRate = 16000;
	private int FrameMs = 20;
	private int FrameSamples = 320;
	private int OutputSampleCount = 640;
	private double AudibleRangeBlocks = 48;
	private float FullVolumeDistanceBlocks = 2f;

	private int AudioAccumulatedMs;
	private long AudioLastTickMs;
	private int MaxFramesPerPump = 6;

	public VoiceEffectPreviewPlayer(ICoreClientAPI api, YHPVCClientConfig cfg, Func<ServerHelloMsg?> getHello)
	{
		this.ClientAPI = api;
		this.ClientConfig = cfg;
		this.GetHelloFunct = getHello;
	}

	public void Play(VoiceEffectPreviewSettings settings)
	{
		if (Disposed) throw new ObjectDisposedException(nameof(VoiceEffectPreviewPlayer));
		ServerHelloMsg hello = GetHelloFunct() ?? throw new InvalidOperationException("YHPVC voice runtime is not ready yet. Race Condition!");
		Stop();

		this.Settings = settings;
		SampleRate = hello.SampleRate;
		FrameMs = Math.Max(1, hello.FrameDurationMS);
		FrameSamples = VoiceMath.FrameSamples(SampleRate, FrameMs);
		OutputSampleCount = FrameSamples * 2;
		AudibleRangeBlocks = Math.Max(0.001, hello.AudibleRangeBlocks);
		FullVolumeDistanceBlocks = Math.Clamp(hello.FullVolumeDistanceBlocks, 0f, Math.Max(0f, (float)AudibleRangeBlocks - 0.01f));

		string clipPath = PickRandomClipPath();
		ClipMono = LoadPcm16WavMono(clipPath, SampleRate);

		MonoFrame = new short[FrameSamples];
		MonoScratch = new float[FrameSamples];
		StereoMix = new float[OutputSampleCount];

		ClipOffset = 0;
		TailFramesRemaining = Math.Max(1, (ReverbTailMs + FrameMs - 1) / FrameMs);
		AudioAccumulatedMs = FrameMs * 3;
		AudioLastTickMs = Environment.TickCount64;
		MaxFramesPerPump = Math.Max(2, 120 / FrameMs);

		OutgoingProcessor.Configure(SampleRate);
		IncomingProcessor.Configure(SampleRate);

		output = new OpenAlStreamingOutput(ClientAPI);
		output.Start(SampleRate, channels: 2);

		TickID = ClientAPI.Event.RegisterGameTickListener(_ => Tick(), 1);
		Tick();
	}

	private string PickRandomClipPath()
	{
		string folder = Path.Combine(ClientAPI.DataBasePath, "ModData", "YHPVC", "testclips");
		Directory.CreateDirectory(folder);

		string[] clips = Directory.GetFiles(folder, "*.wav", SearchOption.TopDirectoryOnly);
		if (clips.Length == 0) { throw new FileNotFoundException("No files found in " + folder); }

		return clips[Random.Shared.Next(clips.Length)];
	}

	private void Tick()
	{
		if (output == null) return;

		long nowMs = Environment.TickCount64;
		int elapsedMs = (int)Math.Clamp(nowMs - AudioLastTickMs, 0, 250);
		AudioLastTickMs = nowMs;

		AudioAccumulatedMs += elapsedMs;
		int framesDue = AudioAccumulatedMs / FrameMs;
		if (framesDue <= 0) return;

		if (framesDue > MaxFramesPerPump)
		{
			framesDue = MaxFramesPerPump;
			AudioAccumulatedMs = 0;
		}
		else { AudioAccumulatedMs -= framesDue * FrameMs; }

		for (int i = 0; i < framesDue; i++) { if (!TryQueueNextFrame()) { Stop(); return; } }
	}

	private bool TryQueueNextFrame()
	{
		if (output == null) return false;

		bool hasClipSamples = ClipOffset < ClipMono.Length;
		if (!hasClipSamples)
		{
			if (TailFramesRemaining <= 0) return false;
			TailFramesRemaining--;
		}

		Array.Clear(MonoFrame, 0, MonoFrame.Length);
		if (hasClipSamples)
		{
			int copy = Math.Min(FrameSamples, ClipMono.Length - ClipOffset);
			Array.Copy(ClipMono, ClipOffset, MonoFrame, 0, copy);
			ClipOffset += copy;
		}

		VoiceOutgoingEffectState outgoingEffects = Settings.BuildOutgoingEffects();
		VoiceIncomingEffectState incomingEffects = Settings.BuildIncomingEffects();

		OutgoingProcessor.ProcessInPlace(MonoFrame, MonoScratch, inputGain: 1f, outgoingEffects);

		Array.Clear(StereoMix, 0, StereoMix.Length);
		VoiceSpatialContext ctx = BuildSpatialContext();
		if (ctx.Gain > 0f)
		{
			PostProcessor.ProcessSpeakerFrame(DebugSpeakerPeerId, MonoFrame, StereoMix, ctx);
			IncomingProcessor.Process(StereoMix, incomingEffects);
		}
		else { IncomingProcessor.ResetForSilence(incomingEffects); }

		PooledPcmFrame frame = output.RentFrame(OutputSampleCount);
		Span<short> pcm = frame.Buffer.AsSpan(0, OutputSampleCount);
		for (int i = 0; i < OutputSampleCount; i++) { pcm[i] = VoiceMath.FloatToPcm16(VoiceMath.SoftLimit(StereoMix[i])); }

		output.Queue(frame);
		return true;
	}

	private VoiceSpatialContext BuildSpatialContext()
	{
		var lp = ClientAPI.World.Player?.Entity?.Pos;
		if (lp == null) throw new InvalidOperationException("Local player position is not available.");

		if (Settings.Source.Dimension != lp.Dimension) { return new VoiceSpatialContext(DebugSpeakerPeerId, 0f, 0f, 0f, 0f, double.MaxValue); }

		double lx = lp.X;
		double ly = lp.Y + lp.DimensionYAdjustment;
		double lz = lp.Z;

		double dx = Settings.Source.X - lx;
		double dy = Settings.Source.Y - ly;
		double dz = Settings.Source.Z - lz;
		double distSq = dx * dx + dy * dy + dz * dz;

		float distanceGain = VoiceMath.DistanceGainWithFullVolumeZone(distSq, FullVolumeDistanceBlocks, AudibleRangeBlocks);
		if (distanceGain <= 0f) { return new VoiceSpatialContext(DebugSpeakerPeerId, 0f, 0f, 0f, 0f, distSq); }

		float pan = VoiceSpatialMath.ComputePan(dx, dz, lp.Yaw) * ClientConfig.StereoPanStrength;
		if (ClientConfig.InvertStereoPan) pan = -pan;

		VoiceMath.EqualPowerPan(pan, out float leftGain, out float rightGain);
		return new VoiceSpatialContext(DebugSpeakerPeerId, distanceGain, leftGain, rightGain, pan, distSq);
	}

	private static short[] LoadPcm16WavMono(string path, int expectedSampleRate)
	{
		using FileStream stream = File.OpenRead(path);
		using BinaryReader reader = new(stream, Encoding.ASCII, leaveOpen: false);

		if (ReadAscii(reader, 4) != "RIFF") throw new InvalidDataException("WAV is missing RIFF header.");
		_ = reader.ReadUInt32();
		if (ReadAscii(reader, 4) != "WAVE") throw new InvalidDataException("WAV is missing WAVE header.");

		ushort audioFormat = 0;
		ushort channels = 0;
		int sampleRate = 0;
		ushort bitsPerSample = 0;
		byte[]? data = null;

		while (stream.Position + 8 <= stream.Length)
		{
			string chunkId = ReadAscii(reader, 4);
			int chunkSize = checked((int)reader.ReadUInt32());
			long chunkStart = stream.Position;

			switch (chunkId)
			{
				case "fmt ":
					audioFormat = reader.ReadUInt16();
					channels = reader.ReadUInt16();
					sampleRate = reader.ReadInt32();
					_ = reader.ReadUInt32();
					_ = reader.ReadUInt16();
					bitsPerSample = reader.ReadUInt16();
				break;

				case "data":
					data = reader.ReadBytes(chunkSize);
				break;
			}

			stream.Position = chunkStart + chunkSize + (chunkSize & 1);
			if (data != null && audioFormat != 0) break;
		}

		if (audioFormat != 1)					throw new InvalidDataException("Only PCM WAV files are supported.");
		if (channels != 1 && channels != 2)		throw new InvalidDataException("Only mono or stereo WAV files are supported.");
		if (bitsPerSample != 16)				throw new InvalidDataException("Only 16-bit PCM WAV files are supported.");
		if (sampleRate != expectedSampleRate)	throw new InvalidDataException("WAV samplerate " + sampleRate +" Hz is not expected " + expectedSampleRate + " Hz.");
		if (data == null)						throw new InvalidDataException("WAV contains no data chunk.");

		int sampleCount = data.Length / 2 / channels;
		short[] mono = new short[sampleCount];

		for (int i = 0; i < sampleCount; i++)
		{
			int o = i * channels * 2;
			short left = BitConverter.ToInt16(data, o);
			if (channels == 1) { mono[i] = left; }
			else
			{
				short right = BitConverter.ToInt16(data, o + 2);
				mono[i] = (short)((left + right) / 2);
			}
		}

		return mono;
	}

	private static string ReadAscii(BinaryReader reader, int count)
	{
		return Encoding.ASCII.GetString(reader.ReadBytes(count));
	}

	public void Stop()
	{
		if (TickID != 0) { try { ClientAPI.Event.UnregisterGameTickListener(TickID); } catch { } TickID = 0; }

		try { output?.Dispose(); } catch { }
		output = null;

		ClipMono = Array.Empty<short>();
		ClipOffset = 0;
		TailFramesRemaining = 0;
		OutgoingProcessor.ResetForSilence();
		IncomingProcessor.Reset();
	}

	public void Dispose()
	{
		if (Disposed) return;
		Disposed = true;
		Stop();
	}
}
