using System;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using YHPVC.Common;

namespace YHPVC.Client;

internal readonly struct VoiceOutgoingEffectState
{
	public VoiceOutgoingEffectState(bool enabled, float underwaterMuffle, float caveReverb, float drunkWarp, float psychedelicWarp)
	{
		Enabled = enabled;
		UnderwaterMuffle = Math.Clamp(underwaterMuffle, 0f, 1f);
		CaveReverb = Math.Clamp(caveReverb, 0f, VoiceImmersiveEffectMapping.MaxPreviewCaveReverbness);
		DrunkWarp = Math.Clamp(drunkWarp, 0f, 1f);
		PsychedelicWarp = Math.Clamp(psychedelicWarp, 0f, 1f);
	}

	public static VoiceOutgoingEffectState Disabled { get; } = new(false, 0f, 0f, 0f, 0f);

	public bool Enabled { get; }
	public float UnderwaterMuffle { get; }
	public float CaveReverb { get; }
	public float DrunkWarp { get; }
	public float PsychedelicWarp { get; }

	public bool HasMuffle	=> Enabled && UnderwaterMuffle > 0.001f;
	public bool HasReverb	=> Enabled && CaveReverb > 0.001f;
	public bool HasWarp		=> Enabled && (DrunkWarp > 0.001f || PsychedelicWarp > 0.001f);
	public bool HasAny		=> HasMuffle || HasReverb || HasWarp;
}

internal readonly struct VoiceIncomingEffectState
{
	public VoiceIncomingEffectState(bool enabled, float underwaterMuffle, float temporalWarp)
	{
		Enabled = enabled;
		UnderwaterMuffle = Math.Clamp(underwaterMuffle, 0f, 1f);
		TemporalWarp = Math.Clamp(temporalWarp, 0f, 1f);
	}

	public static VoiceIncomingEffectState Disabled { get; } = new(false, 0f, 0f);

	public bool Enabled { get; }
	public float UnderwaterMuffle { get; }
	public float TemporalWarp { get; }

	public bool HasMuffle 	=> Enabled && UnderwaterMuffle > 0.001f;
	public bool HasWarp 	=> Enabled && TemporalWarp > 0.001f;
	public bool HasAny 		=> HasMuffle || HasWarp;
}


internal static class VoiceImmersiveEffectMapping
{
	public const float MaxGameplayIntoxication = 1.1f;
	public const float DrunkIntoxicationStep = 0.005f;
	public const float MaxGameplayPsychedelic = 2f;
	public const float PsychedelicStep = 0.005f;
	public const float MaxPreviewCaveReverbness = 7f;
	public const float CaveReverbnessStep = 0.1f;
	public const float TemporalGlitchThreshold = 0.5f;
	public const float TemporalVoiceWarpWhenActive = 0.65f;

	public static float UnderwaterMuffleFromSubmerged(bool submerged) => submerged ? 1f : 0f;
	public static float CaveReverbFromReverbness(float reverbness, bool submerged) { if (submerged) return 0f; return Math.Clamp(reverbness, 0f, MaxPreviewCaveReverbness); }
	public static float TemporalWarpFromGlitchStrength(float glitchStrength) { return glitchStrength > TemporalGlitchThreshold ? TemporalVoiceWarpWhenActive : 0f; }
	public static float DrunkWarpFromIntoxication(float intoxication) { return Math.Clamp(intoxication / MaxGameplayIntoxication, 0f, 1f); }
	public static float PsychedelicWarpFromPsychedelic(float psychedelic) { return Math.Clamp(psychedelic / MaxGameplayPsychedelic, 0f, 1f); }
}

internal sealed class VoiceEnvironmentSampler : IDisposable
{
	private const int TickIntervalMs = 100;

	private readonly ICoreClientAPI ClientAPI;
	private readonly VoiceSoftwareMixer SoftwareMixer;

	private VoiceCaptureService? Capture;
	private VoiceOutgoingEffectState Outgoing = VoiceOutgoingEffectState.Disabled;
	private VoiceIncomingEffectState Incoming = VoiceIncomingEffectState.Disabled;
	private long TickID;
	private bool SampleFailureLogged;
	private byte LastLoggedOutgoingMask = byte.MaxValue;
	private byte LastLoggedIncomingMask = byte.MaxValue;

	public VoiceEnvironmentSampler(ICoreClientAPI api, VoiceCaptureService? capture, VoiceSoftwareMixer mixer)
	{
		this.ClientAPI = api;
		this.Capture = capture;
		this.SoftwareMixer = mixer;
	}

	public void Start()
	{
		ClientAPI.Logger.Notification("[YHPVC] Immersive voice effect sampler started.");
		SampleAndApply();
		TickID = ClientAPI.Event.RegisterGameTickListener(_ => SampleAndApply(), TickIntervalMs);
	}

	public void SetCapture(VoiceCaptureService? capture)
	{
		this.Capture = capture;
		capture?.SetOutgoingEffects(Outgoing);
	}

	private void SampleAndApply()
	{
		try { ReadStates(out Outgoing, out Incoming); }
		catch (Exception ex)
		{
			if (!SampleFailureLogged)
			{
				SampleFailureLogged = true;
				ClientAPI.Logger.Warning("[YHPVC] Immersive voice effect sampling failed: {0}", ex);
			}

			Outgoing = VoiceOutgoingEffectState.Disabled;
			Incoming = VoiceIncomingEffectState.Disabled;
		}

		Capture?.SetOutgoingEffects(Outgoing);
		SoftwareMixer.SetIncomingEffects(Incoming);
		LogStateChanges(Outgoing, Incoming);
	}


	private void LogStateChanges(VoiceOutgoingEffectState outgoing, VoiceIncomingEffectState incoming)
	{
		byte outgoingMask = 0;
		if (outgoing.HasMuffle) outgoingMask |= 1;
		if (outgoing.HasReverb) outgoingMask |= 2;
		if (outgoing.DrunkWarp > 0.001f) outgoingMask |= 4;
		if (outgoing.PsychedelicWarp > 0.001f) outgoingMask |= 8;

		byte incomingMask = 0;
		if (incoming.HasMuffle) incomingMask |= 1;
		if (incoming.HasWarp) incomingMask |= 2;

		if (outgoingMask == LastLoggedOutgoingMask && incomingMask == LastLoggedIncomingMask) return;

		LastLoggedOutgoingMask = outgoingMask;
		LastLoggedIncomingMask = incomingMask;

		ClientAPI.Logger.Notification
		(
			"[YHPVC] Immersive voice effects sampled: " +
			"outgoing={0}, incoming={1}, water={2:0.00}, caveReverb={3:0.00}, drunkWarp={4:0.00}, psychedelicWarp={5:0.00}, temporalWarp={6:0.00}.",
			DescribeOutgoingEffects(outgoing),
			DescribeIncomingEffects(incoming),
			outgoing.UnderwaterMuffle,
			outgoing.CaveReverb,
			outgoing.DrunkWarp,
			outgoing.PsychedelicWarp,
			incoming.TemporalWarp
		);
	}

	private static string DescribeOutgoingEffects(VoiceOutgoingEffectState state)
	{
		if (!state.HasAny) return "none";

		string description = string.Empty;
		if (state.HasMuffle) description = AppendEffect(description, "muffle");
		if (state.HasReverb) description = AppendEffect(description, "reverb");
		if (state.DrunkWarp > 0.001f) description = AppendEffect(description, "drunkWarp");
		if (state.PsychedelicWarp > 0.001f) description = AppendEffect(description, "psychedelicWarp");

		return description;
	}

	private static string DescribeIncomingEffects(VoiceIncomingEffectState state)
	{
		if (!state.HasAny) return "none";

		string description = string.Empty;
		if (state.HasMuffle) description = AppendEffect(description, "muffle");
		if (state.HasWarp) description = AppendEffect(description, "temporalWarp");

		return description;
	}

	private static string AppendEffect(string current, string effect) { return current.Length == 0 ? effect : current + "+" + effect; }

	private void ReadStates(out VoiceOutgoingEffectState outgoing, out VoiceIncomingEffectState incoming)
	{
		var uniforms = ClientAPI.Render.ShaderUniforms;

		float temporalWarp = VoiceImmersiveEffectMapping.TemporalWarpFromGlitchStrength(uniforms.GlitchStrength);

		float intoxication = ClientAPI.World.Player?.Entity?.WatchedAttributes.GetFloat("intoxication") ?? 0f;
		float drunkWarp = VoiceImmersiveEffectMapping.DrunkWarpFromIntoxication(intoxication);

		float psychedelic = ClientAPI.World.Player?.Entity?.WatchedAttributes.GetFloat("psychedelic") ?? 0f;
		float psychedelicWarp = VoiceImmersiveEffectMapping.PsychedelicWarpFromPsychedelic(psychedelic);

		bool submerged = false;
		if (ClientAPI.World is ClientMain clientMain)
		{
			var props = clientMain.playerProperties;
			submerged = props.EyesInWaterDepth > 0f || props.EyesInLavaDepth > 0f;
		}

		// Fallback
		submerged |= uniforms.CameraUnderwater > 0f;

		float underwaterMuffle = VoiceImmersiveEffectMapping.UnderwaterMuffleFromSubmerged(submerged);
		float caveReverb = VoiceImmersiveEffectMapping.CaveReverbFromReverbness(SystemSoundEngine.NowReverbness, submerged);

		outgoing = new VoiceOutgoingEffectState
		(
			enabled: true,
			underwaterMuffle: underwaterMuffle,
			caveReverb: caveReverb,
			drunkWarp: drunkWarp,
			psychedelicWarp: psychedelicWarp
		);

		incoming = new VoiceIncomingEffectState
		(
			enabled: true,
			underwaterMuffle: underwaterMuffle,
			temporalWarp: temporalWarp
		);
	}

	public void Dispose()
	{
		if (TickID != 0)
		{
			try { ClientAPI.Event.UnregisterGameTickListener(TickID); } catch { }
			TickID = 0;
		}

		try { Capture?.SetOutgoingEffects(VoiceOutgoingEffectState.Disabled); } catch { }
		try { SoftwareMixer.SetIncomingEffects(VoiceIncomingEffectState.Disabled); } catch { }
		Capture = null;
	}
}

internal sealed class VoiceOutgoingEffectsProcessor
{
	private readonly VoiceFractionalDelayWarp DrunkWarp = new();
	private readonly VoiceMuffleFilter Muffle = new();
	private readonly VoiceMonoRoomReverb Reverb = new();

	private int SampleRate;

	public void Configure(int sampleRate)
	{
		this.SampleRate = Math.Max(1, sampleRate);
		DrunkWarp.Configure(this.SampleRate, channels: 1);
		Muffle.Configure(this.SampleRate, channels: 1, cutoffHz: 900f);
		Reverb.Configure(this.SampleRate);
	}

	public bool NeedsProcess(float inputGain, VoiceOutgoingEffectState state) { return Math.Abs(inputGain - 1f) > 0.0001f || state.HasAny || Muffle.IsActive; }

	public void ProcessInPlace(short[] pcm, Span<float> scratch, float inputGain, VoiceOutgoingEffectState state)
	{
		int sampleCount = Math.Min(pcm.Length, scratch.Length);
		if (sampleCount <= 0) return;

		Span<float> mono = scratch[..sampleCount];
		float gain = Math.Clamp(inputGain, 0f, 4f);

		for (int i = 0; i < sampleCount; i++) { mono[i] = (pcm[i] / 32768f) * gain; }
		
		if (state.HasWarp) { DrunkWarp.Process(mono, SampleRate, channels: 1, drunkAmount: state.DrunkWarp, psychedelicAmount: state.PsychedelicWarp, temporalAmount: 0f); }
		else { DrunkWarp.Reset(); }

		if (state.HasMuffle || Muffle.IsActive) { Muffle.Process(mono, SampleRate, channels: 1, targetWet: state.UnderwaterMuffle, cutoffHz: 900f); }

		if (state.HasReverb) { Reverb.Process(mono, SampleRate, state.CaveReverb); }
		else { Reverb.Reset(); }

		for (int i = 0; i < sampleCount; i++) { pcm[i] = VoiceMath.FloatToPcm16(mono[i]); }
	}

	public void ResetForSilence()
	{
		DrunkWarp.Reset();
		Muffle.ResetForSilence(keepWet: false);
		Reverb.Reset();
	}

}

internal sealed class VoiceIncomingEffectsProcessor
{
	private readonly VoiceFractionalDelayWarp TemporalWarp = new();
	private readonly VoiceMuffleFilter UnderwaterMuffle = new();

	private int SampleRate;

	public void Configure(int sampleRate)
	{
		this.SampleRate = Math.Max(1, sampleRate);
		TemporalWarp.Configure(this.SampleRate, channels: 2);
		UnderwaterMuffle.Configure(this.SampleRate, channels: 2, cutoffHz: 900f);
	}

	public void Process(Span<float> interleavedStereo, VoiceIncomingEffectState state)
	{
		bool needsWarp = state.HasWarp;
		bool needsMuffle = state.HasMuffle || UnderwaterMuffle.IsActive;

		if (needsWarp) { TemporalWarp.Process(interleavedStereo, SampleRate, channels: 2, drunkAmount: 0f, psychedelicAmount: 0f, temporalAmount: state.TemporalWarp); }
		else { TemporalWarp.Reset(); }

		if (needsMuffle) { UnderwaterMuffle.Process(interleavedStereo, SampleRate, channels: 2, targetWet: state.UnderwaterMuffle, cutoffHz: 900f); }
	}

	public void ResetForSilence(VoiceIncomingEffectState state)
	{
		TemporalWarp.Reset();
		UnderwaterMuffle.ResetForSilence(state.HasMuffle);
	}

	public void Reset()
	{
		TemporalWarp.Reset();
		UnderwaterMuffle.Reset();
	}
}

internal sealed class VoiceMuffleFilter
{
	private const float InactiveWetThreshold = 0.0001f;

	private float StageA0;
	private float StageB0;
	private float StageA1;
	private float StageB1;
	private float Wet;

	private int SampleRate;
	private int Channels;
	private float CutoffHz;
	private float Alpha;

	public bool IsActive => Wet > InactiveWetThreshold;

	public void Configure(int sampleRate, int channels, float cutoffHz)
	{
		int clampedSampleRate = Math.Max(1, sampleRate);
		int clampedChannels = Math.Clamp(channels, 1, 2);
		float clampedCutoff = Math.Clamp(cutoffHz, 80f, clampedSampleRate * 0.45f);

		if (this.SampleRate == clampedSampleRate && this.Channels == clampedChannels && Math.Abs(this.CutoffHz - clampedCutoff) < 0.001f && Alpha > 0f) { return; }

		this.SampleRate = clampedSampleRate;
		this.Channels = clampedChannels;
		this.CutoffHz = clampedCutoff;
		Alpha = 1f - MathF.Exp(-2f * MathF.PI * clampedCutoff / clampedSampleRate);
	}

	public void Process(Span<float> interleaved, int sampleRate, int channels, float targetWet, float cutoffHz)
	{
		int clampedChannels = Math.Clamp(channels, 1, 2);
		if (interleaved.Length < clampedChannels) return;

		targetWet = Math.Clamp(targetWet, 0f, 1f);
		if (targetWet <= 0f && Wet <= InactiveWetThreshold) { Wet = 0f; return; }

		Configure(sampleRate, clampedChannels, cutoffHz);

		int frameCount = interleaved.Length / clampedChannels;
		if (frameCount <= 0) return;

		float wetStep = (targetWet - Wet) / frameCount;

		for (int frame = 0; frame < frameCount; frame++)
		{
			Wet += wetStep;

			int o = frame * clampedChannels;
			float in0 = interleaved[o];

			StageA0 += (in0 - StageA0) * Alpha;
			StageB0 += (StageA0 - StageB0) * Alpha;
			interleaved[o] = in0 + (StageB0 - in0) * Wet;

			if (clampedChannels == 2)
			{
				float in1 = interleaved[o + 1];

				StageA1 += (in1 - StageA1) * Alpha;
				StageB1 += (StageA1 - StageB1) * Alpha;
				interleaved[o + 1] = in1 + (StageB1 - in1) * Wet;
			}
		}

		if (targetWet <= 0f && Wet <= InactiveWetThreshold) { Reset(); }
	}

	public void ResetForSilence(bool keepWet)
	{
		StageA0 = 0f;
		StageB0 = 0f;
		StageA1 = 0f;
		StageB1 = 0f;

		Wet = keepWet ? 1f : 0f;
	}

	public void Reset()
	{
		StageA0 = 0f;
		StageB0 = 0f;
		StageA1 = 0f;
		StageB1 = 0f;

		Wet = 0f;
	}
}

internal sealed class VoiceFractionalDelayWarp
{
	private const float TwoPi = MathF.PI * 2f;

	private float[] Delay = Array.Empty<float>();
	private int DelayFrames;
	private int Channels = 2;
	private int WriteFrame;
	private int SampleRate;

	private float DrunkPhase;
	private float PsychedelicPhase;
	private float StormPhaseA;
	private float StormPhaseB;
	private uint RNGState = 0x9E3779B9u;
	private float StormJitter;
	private int StormJitterSamplesRemaining;
	private bool HasBufferedAudio;

	public void Configure(int sampleRate, int channels)
	{
		int clampedSampleRate = Math.Max(1, sampleRate);
		int clampedChannels = Math.Clamp(channels, 1, 2);

		if (this.SampleRate == clampedSampleRate && this.Channels == clampedChannels && DelayFrames > 0) return;

		this.SampleRate = clampedSampleRate;
		this.Channels = clampedChannels;
		DelayFrames = Math.Max(256, this.SampleRate / 5); // 200 ms maximum delay storage.
		Delay = new float[DelayFrames * this.Channels];
		WriteFrame = 0;
		DrunkPhase = 0f;
		PsychedelicPhase = 0.9f;
		StormPhaseA = 0f;
		StormPhaseB = 1.7f;
		StormJitter = 0f;
		StormJitterSamplesRemaining = 0;
		HasBufferedAudio = false;
	}

	public void Process(Span<float> interleaved, int sampleRate, int channels, float drunkAmount, float psychedelicAmount, float temporalAmount)
	{
		int clampedChannels = Math.Clamp(channels, 1, 2);
		if (interleaved.Length < clampedChannels) return;

		float drunk = Math.Clamp(drunkAmount, 0f, 1f);
		float psychedelic = Math.Clamp(psychedelicAmount, 0f, 1f);
		float temporal = Math.Clamp(temporalAmount, 0f, 1f);
		float wet = Math.Clamp(drunk * 0.62f + psychedelic * 0.72f + temporal * 0.72f, 0f, 0.88f);
		if (wet <= 0.001f) return;

		Configure(sampleRate, clampedChannels);

		int frameCount = interleaved.Length / clampedChannels;
		if (frameCount <= 0) return;

		HasBufferedAudio = true;

		bool hasDrunk = drunk > 0.001f;
		bool hasPsychedelic = psychedelic > 0.001f;
		float baseDelayMs = 7f + drunk * 12f + psychedelic * 15f + temporal * 8f;
		float drunkDepthMs = drunk * 6.5f;
		float psychedelicDepthMs = psychedelic * 9.75f;
		float stormDepthMs = temporal * 8.5f;
		float drunkRate = 3.8f;
		float psychedelicRate = drunkRate * 1.5f;
		float stormRateA = 5.5f + temporal * 10f;
		float stormRateB = 11.0f + temporal * 17f;

		float drunkPhaseStep = TwoPi * drunkRate / this.SampleRate;
		float psychedelicPhaseStep = TwoPi * psychedelicRate / this.SampleRate;
		float stormPhaseStepA = TwoPi * stormRateA / this.SampleRate;
		float stormPhaseStepB = TwoPi * stormRateB / this.SampleRate;

		for (int frame = 0; frame < frameCount; frame++)
		{
			if (StormJitterSamplesRemaining-- <= 0)
			{
				StormJitter = (NextRandom01() * 2f - 1f) * temporal;
				StormJitterSamplesRemaining = Math.Max(1, this.SampleRate / 20); // 50 ms
			}

			int o = frame * clampedChannels;
			int writeOffset = WriteFrame * clampedChannels;

			for (int channel = 0; channel < clampedChannels; channel++) { Delay[writeOffset + channel] = interleaved[o + channel]; }

			float drunkOffset = hasDrunk ? MathF.Sin(DrunkPhase) * drunkDepthMs : 0f;
			float psychedelicOffset = hasPsychedelic ? MathF.Sin(PsychedelicPhase) * psychedelicDepthMs : 0f;
			float delayMs = baseDelayMs + drunkOffset + psychedelicOffset + (MathF.Sin(StormPhaseA) * 0.65f + MathF.Sin(StormPhaseB) * 0.35f + StormJitter) * stormDepthMs;

			float delayFramesBack = Math.Clamp(delayMs * this.SampleRate / 1000f, 1f, DelayFrames - 2f);
			float read = WriteFrame - delayFramesBack;
			while (read < 0f) read += DelayFrames;

			int index0 = (int)read;
			int index1 = index0 + 1;
			if (index1 >= DelayFrames) index1 = 0;

			float frac = read - index0;
			int o0 = index0 * clampedChannels;
			int o1 = index1 * clampedChannels;

			for (int channel = 0; channel < clampedChannels; channel++)
			{
				float input = interleaved[o + channel];
				float delayed = Delay[o0 + channel] + (Delay[o1 + channel] - Delay[o0 + channel]) * frac;
				interleaved[o + channel] = input + (delayed - input) * wet;
			}

			WriteFrame++;
			if (WriteFrame >= DelayFrames) WriteFrame = 0;

			if (hasDrunk)
			{
				DrunkPhase += drunkPhaseStep;
				if (DrunkPhase >= TwoPi) DrunkPhase -= TwoPi;
			}

			if (hasPsychedelic)
			{
				PsychedelicPhase += psychedelicPhaseStep;
				if (PsychedelicPhase >= TwoPi) PsychedelicPhase -= TwoPi;
			}

			StormPhaseA += stormPhaseStepA;
			if (StormPhaseA >= TwoPi) StormPhaseA -= TwoPi;

			StormPhaseB += stormPhaseStepB;
			if (StormPhaseB >= TwoPi) StormPhaseB -= TwoPi;
		}
	}

	private float NextRandom01()
	{
		unchecked { RNGState = RNGState * 1664525u + 1013904223u; }
		return (RNGState >> 8) * (1f / 16777216f);
	}

	public void Reset()
	{
		if (!HasBufferedAudio && WriteFrame == 0 && StormJitterSamplesRemaining == 0) return;

		Array.Clear(Delay, 0, Delay.Length);
		WriteFrame = 0;
		DrunkPhase = 0f;
		PsychedelicPhase = 0.9f;
		StormPhaseA = 0f;
		StormPhaseB = 1.7f;
		StormJitter = 0f;
		StormJitterSamplesRemaining = 0;
		HasBufferedAudio = false;
	}
}

internal sealed class VoiceMonoRoomReverb
{
	private float[] DelayA = Array.Empty<float>();
	private float[] DelayB = Array.Empty<float>();
	private float[] DelayC = Array.Empty<float>();
	private int PosA;
	private int PosB;
	private int PosC;
	private int SampleRate;
	private float DampA;
	private float DampB;
	private float DampC;

	public void Configure(int sampleRate)
	{
		int clampedSampleRate = Math.Max(1, sampleRate);
		if (this.SampleRate == clampedSampleRate && DelayA.Length > 0) return;

		this.SampleRate = clampedSampleRate;
		DelayA = new float[Math.Max(1, clampedSampleRate * 29 / 1000)];
		DelayB = new float[Math.Max(1, clampedSampleRate * 37 / 1000)];
		DelayC = new float[Math.Max(1, clampedSampleRate * 43 / 1000)];
		Reset();
	}

	public void Process(Span<float> mono, int sampleRate, float reverbness)
	{
		if (mono.Length == 0) return;

		float amount = Math.Clamp(reverbness, 0f, VoiceImmersiveEffectMapping.MaxPreviewCaveReverbness);
		float deep = Math.Clamp((amount - 1f) / Math.Max(0.001f, VoiceImmersiveEffectMapping.MaxPreviewCaveReverbness - 1f), 0f, 1f);
		float wet = amount <= 1f ? amount * 0.7f : 0.7f + deep * 0.2f;
		if (wet <= 0.001f) { Reset(); return; }

		Configure(sampleRate);
		float dry = 1f - deep * 0.18f;
		float dampCoefA = 0.32f - deep * 0.14f;
		float dampCoefB = 0.28f - deep * 0.12f;
		float dampCoefC = 0.24f - deep * 0.10f;
		float feedbackA = 0.42f + deep * 0.26f;
		float feedbackB = 0.35f + deep * 0.24f;
		float feedbackC = 0.30f + deep * 0.22f;

		for (int i = 0; i < mono.Length; i++)
		{
			float input = mono[i];

			float tapA = DelayA[PosA];
			float tapB = DelayB[PosB];
			float tapC = DelayC[PosC];

			DampA += (tapA - DampA) * dampCoefA;
			DampB += (tapB - DampB) * dampCoefB;
			DampC += (tapC - DampC) * dampCoefC;

			float wetSample = (DampA + DampB + DampC) * (1f / 3f);

			DelayA[PosA] = input + DampA * feedbackA;
			DelayB[PosB] = input + DampB * feedbackB;
			DelayC[PosC] = input + DampC * feedbackC;

			mono[i] = input * dry + wetSample * wet;

			if (++PosA >= DelayA.Length) PosA = 0;
			if (++PosB >= DelayB.Length) PosB = 0;
			if (++PosC >= DelayC.Length) PosC = 0;
		}
	}

	public void Reset()
	{
		Array.Clear(DelayA, 0, DelayA.Length);
		Array.Clear(DelayB, 0, DelayB.Length);
		Array.Clear(DelayC, 0, DelayC.Length);
		PosA = 0;
		PosB = 0;
		PosC = 0;
		DampA = 0f;
		DampB = 0f;
		DampC = 0f;
	}
}
