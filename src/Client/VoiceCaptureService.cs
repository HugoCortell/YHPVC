using System;
using System.Threading;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;
using YHPVC.Common;

namespace YHPVC.Client;

internal delegate void LocalMonitorFrameHandler(ReadOnlySpan<short> pcm);

internal sealed class VoiceCaptureService : IDisposable
{
	private readonly ICoreClientAPI ClientAPI;
	private readonly YHPVCClientConfig ClientConfig;
	private readonly VoiceWaveformBuffer Waveform;
	private readonly Func<bool> ShouldTransmitFunct;
	private readonly Func<bool> IsMutedFunct;
	private readonly Action<ClientVoiceFrame> OnFrame;
	private readonly LocalMonitorFrameHandler OnLocalMonitorFrame;

	private ALCaptureDevice CaptureDevice = ALCaptureDevice.Null;
	private OpusEncoderAdapter? Encoder;
	private RNNoiseDenoiser? Denoiser;
	private VoiceResampler? RawCaptureResampler;
	private readonly VoiceOutgoingEffectsProcessor OutgoingEffects = new();
	private VoiceOutgoingEffectState OutgoingEffectState = VoiceOutgoingEffectState.Disabled;

	private int NetworkSampleRate;
	private int NetworkFrameSamples;
	private int CaptureSampleRate;
	private int CaptureFrameSamples;
	private int FrameDurationMS;
	private int MinBitrateKBPS;
	private int MaxBitrateKBPS;
	private int MaxPayloadBytes = 430;

	private short[] CaptureScratch = Array.Empty<short>();
	private short[] CaptureFrameScratch = Array.Empty<short>();
	private short[] NetworkFrameScratch = Array.Empty<short>();
	private float[] Raw48Scratch = Array.Empty<float>();
	private float[] OutgoingEffectScratch = Array.Empty<float>();
	private short[] LocalMonitorScratch = Array.Empty<short>();
	private int FrameFill;
	private ushort Sequence;
	private long VadUntilMS;
	private long LastTransmittedMs;
	private bool DenoiserFailureLogged;
	private bool OutgoingEffectScratchFailureLogged;
	private byte LastLoggedOutgoingProcessMask;
	private volatile bool Disposed;

	public VoiceCaptureService
	(
		ICoreClientAPI api,
		YHPVCClientConfig clientCfg,
		VoiceWaveformBuffer waveform,
		Func<bool> shouldTransmit,
		Func<bool> isMuted,
		Action<ClientVoiceFrame> onFrame,
		LocalMonitorFrameHandler onLocalMonitorFrame
	)
	{
		this.ClientAPI = api;
		this.ClientConfig = clientCfg;
		this.Waveform = waveform;
		this.ShouldTransmitFunct = shouldTransmit;
		this.IsMutedFunct = isMuted;
		this.OnFrame = onFrame;
		this.OnLocalMonitorFrame = onLocalMonitorFrame;
	}

	public bool Running => CaptureDevice != ALCaptureDevice.Null && Encoder != null;

	public bool IsTransmittingRecently(int windowMs)
	{
		long last = Volatile.Read(ref LastTransmittedMs);
		return last > 0 && Environment.TickCount64 - last <= Math.Max(1, windowMs);
	}

	public void SetOutgoingEffects(VoiceOutgoingEffectState state) { OutgoingEffectState = state; }

	public void Start(ServerHelloMsg hello)
	{
		Disposed = false;
		ClientAPI.Event.EnqueueMainThreadTask(() => StartMainThread(hello), "yhpvc-start-capture");
	}

	private void ConfigureForHello(ServerHelloMsg hello)
	{
		NetworkSampleRate = hello.SampleRate;
		FrameDurationMS = hello.FrameDurationMS;
		NetworkFrameSamples = VoiceMath.FrameSamples(NetworkSampleRate, FrameDurationMS);
		OutgoingEffects.Configure(NetworkSampleRate);
		OutgoingEffectScratch = new float[NetworkFrameSamples];
		LocalMonitorScratch = new short[NetworkFrameSamples];
		MinBitrateKBPS = hello.MinBitrateKbps;
		MaxBitrateKBPS = hello.MaxBitrateKbps;
		MaxPayloadBytes = hello.MaxVoicePayloadBytes > 0 ? hello.MaxVoicePayloadBytes : 430;

		CaptureSampleRate = NetworkSampleRate;
		CaptureFrameSamples = NetworkFrameSamples;
		FrameFill = 0;
		Sequence = 0;
		DenoiserFailureLogged = false;
		OutgoingEffectScratchFailureLogged = false;
		LastLoggedOutgoingProcessMask = 0;
		Volatile.Write(ref LastTransmittedMs, 0);
	}

	private void StartMainThread(ServerHelloMsg hello)
	{
		if (Disposed) return;

		try
		{
			VoiceOutgoingEffectState preservedEffects = OutgoingEffectState;
			StopMainThread(clearOutgoingEffects: false);
			OutgoingEffectState = preservedEffects;

			ConfigureForHello(hello);

			ClientAPI.Logger.Notification
			(
				"[YHPVC] Voice capture start requested: {0} Hz network rate, {1} ms frames, effect scratch {2} samples.",
				NetworkSampleRate,
				FrameDurationMS,
				OutgoingEffectScratch.Length
			);

			Encoder = new OpusEncoderAdapter
			(
				NetworkSampleRate,
				FrameDurationMS,
				Math.Clamp(hello.DefaultBitrateKBPS, MinBitrateKBPS, MaxBitrateKBPS),
				hello.OpusComplexity,
				hello.DTX,
				hello.FEC
			);

			string? deviceName = string.IsNullOrWhiteSpace(ClientConfig.InputDevice) ? null : ClientConfig.InputDevice;

			if (ClientConfig.NoiseSuppressionEnabled && RNNoiseNativeLoader.TryEnsureLoaded(ClientAPI))
			{
				if (TryStartCaptureDevice(deviceName, useNoiseSuppression: true)) { return; }
				ClientAPI.Logger.Warning("[YHPVC] RNNoise capture at 48 kHz failed; falling back to normal capture without noise suppression.");
			}

			if (!TryStartCaptureDevice(deviceName, useNoiseSuppression: false))
			{
				Encoder.Dispose(); Encoder = null;
				ClientAPI.Logger.Warning("[YHPVC] Failed to open OpenAL capture device.");
			}
		}
		catch (Exception ex)
		{
			ClientAPI.Logger.Warning("[YHPVC] Failed to start voice capture: {0}", ex);
			StopMainThread();
		}
	}

	private bool TryStartCaptureDevice(string? deviceName, bool useNoiseSuppression)
	{
		RNNoiseDenoiser? newDenoiser = null;
		VoiceResampler? newRawCaptureResampler = null;
		ALCaptureDevice newCaptureDevice = ALCaptureDevice.Null;

		try
		{
			int newCaptureSampleRate = useNoiseSuppression ? RNNoiseDenoiser.RNNoiseSampleRate : NetworkSampleRate;
			int newCaptureFrameSamples = VoiceMath.FrameSamples(newCaptureSampleRate, FrameDurationMS);

			if (useNoiseSuppression)
			{
				newDenoiser = new RNNoiseDenoiser(NetworkSampleRate, FrameDurationMS, ClientConfig.NoiseSuppressionMix);
				newRawCaptureResampler = new VoiceResampler(RNNoiseDenoiser.RNNoiseSampleRate, NetworkSampleRate);
			}

			newCaptureDevice = ALC.CaptureOpenDevice(deviceName, newCaptureSampleRate, ALFormat.Mono16, newCaptureSampleRate);
			if (newCaptureDevice == ALCaptureDevice.Null)
			{
				newDenoiser?.Dispose();
				return false;
			}

			CaptureSampleRate = newCaptureSampleRate;
			CaptureFrameSamples = newCaptureFrameSamples;
			Denoiser = newDenoiser;
			RawCaptureResampler = newRawCaptureResampler;

			CaptureScratch = new short[CaptureFrameSamples * 4];
			CaptureFrameScratch = new short[CaptureFrameSamples];
			NetworkFrameScratch = useNoiseSuppression ? new short[NetworkFrameSamples] : Array.Empty<short>();
			Raw48Scratch = useNoiseSuppression ? new float[CaptureFrameSamples] : Array.Empty<float>();
			OutgoingEffectScratch = new float[NetworkFrameSamples];
			LocalMonitorScratch = new short[NetworkFrameSamples];
			FrameFill = 0;

			CaptureDevice = newCaptureDevice;
			ALC.CaptureStart(CaptureDevice);

			if (Denoiser != null)
			{
				ClientAPI.Logger.Notification
				(
					"[YHPVC] Voice capture started at {0} Hz with RNNoise, downsampling to {1} Hz, {2} ms frames, outgoing effect scratch {3} samples.",
					CaptureSampleRate,
					NetworkSampleRate,
					FrameDurationMS,
					OutgoingEffectScratch.Length
				);
			}
			else
			{
				ClientAPI.Logger.Notification
				(
					"[YHPVC] Voice capture started at {0} Hz, {1} ms frames, outgoing effect scratch {2} samples.",
					CaptureSampleRate,
					FrameDurationMS,
					OutgoingEffectScratch.Length
				);
			}

			return true;
		}
		catch (Exception ex)
		{
			if (newCaptureDevice != ALCaptureDevice.Null) { try { ALC.CaptureCloseDevice(newCaptureDevice); } catch { } }
			try { newDenoiser?.Dispose(); } catch { }

			if (useNoiseSuppression) { ClientAPI.Logger.Warning("[YHPVC] Failed to initialize RNNoise capture path: {0}", ex.Message); }

			return false;
		}
	}

	public void ApplyCodecControl(CodecControlMsg msg)
	{
		int bitrate = Math.Clamp(msg.TargetBitrateKbps, MinBitrateKBPS, MaxBitrateKBPS);
		Encoder?.SetBitrateKbps(bitrate);
		Encoder?.SetFec(msg.FEC, msg.PacketLossPercent);
	}

	public void Tick()
	{
		if (CaptureDevice == ALCaptureDevice.Null || Encoder == null) return;

		try
		{
			int available = ALC.GetInteger(CaptureDevice, AlcGetInteger.CaptureSamples);
			while (available >= CaptureFrameSamples)
			{
				int toRead = Math.Min(available, CaptureScratch.Length);
				ALC.CaptureSamples(CaptureDevice, CaptureScratch, toRead);
				available -= toRead;

				int offset = 0;
				while (toRead > 0)
				{
					int copy = Math.Min(CaptureFrameSamples - FrameFill, toRead);
					Array.Copy(CaptureScratch, offset, CaptureFrameScratch, FrameFill, copy);
					FrameFill += copy;
					offset += copy;
					toRead -= copy;

					if (FrameFill == CaptureFrameSamples)
					{
						ProcessCapturedFrame(CaptureFrameScratch);
						FrameFill = 0;
					}
				}
			}
		}
		catch (Exception ex)
		{
			ClientAPI.Logger.Warning("[YHPVC] Voice capture tick failed: {0}", ex);
			Stop();
		}
	}

	private void ProcessCapturedFrame(short[] capturePcm)
	{
		if (Denoiser != null)
		{
			if (Denoiser.ProcessFrame(capturePcm, NetworkFrameScratch))
			{
				ProcessNetworkFrame(NetworkFrameScratch);
				return;
			}

			try { Denoiser.Dispose(); } catch { }
			Denoiser = null;

			if (!DenoiserFailureLogged)
			{
				DenoiserFailureLogged = true;
				ClientAPI.Logger.Warning("[YHPVC] RNNoise processing failed; continuing with raw 48 kHz capture downsampled to the network rate.");
			}
		}

		if (CaptureSampleRate == NetworkSampleRate)
		{
			ProcessNetworkFrame(capturePcm);
			return;
		}

		if (DownsampleRawCapture(capturePcm, NetworkFrameScratch)) { ProcessNetworkFrame(NetworkFrameScratch); }
	}

	private bool DownsampleRawCapture(short[] capturePcm, short[] outputNetworkPcm)
	{
		if (RawCaptureResampler == null) return false;
		if (Raw48Scratch.Length < CaptureFrameSamples) return false;
		if (outputNetworkPcm.Length < NetworkFrameSamples) return false;

		for (int i = 0; i < CaptureFrameSamples; i++) { Raw48Scratch[i] = capturePcm[i]; }
		RawCaptureResampler.Process48ToNetwork(Raw48Scratch.AsSpan(0, CaptureFrameSamples), outputNetworkPcm.AsSpan(0, NetworkFrameSamples));

		return true;
	}

	private void EnqueueLocalMonitorFrame(short[] pcm)
	{
		if (!ClientConfig.HearSelf || NetworkFrameSamples <= 0) return;

		int sampleCount = Math.Min(NetworkFrameSamples, pcm.Length);
		if (sampleCount <= 0) return;

		float gain = Math.Clamp(ClientConfig.InputGain, 0f, 4f);
		if (Math.Abs(gain - 1f) <= 0.0001f) { OnLocalMonitorFrame(pcm.AsSpan(0, sampleCount)); return; }

		if (LocalMonitorScratch.Length < sampleCount) LocalMonitorScratch = new short[sampleCount];

		for (int i = 0; i < sampleCount; i++) { LocalMonitorScratch[i] = VoiceMath.FloatToPcm16((pcm[i] / 32768f) * gain); }

		OnLocalMonitorFrame(LocalMonitorScratch.AsSpan(0, sampleCount));
	}

	private void ProcessNetworkFrame(short[] pcm)
	{
		if (Encoder == null) return;

		bool pttOrOpen = ShouldTransmitFunct();
		float rms = VoiceMath.ComputeRms(pcm);
		Waveform.PushLevel(Math.Clamp(rms * ClientConfig.InputGain * 8f, 0f, 1f));

		if (IsMutedFunct()) { OutgoingEffects.ResetForSilence(); return; }

		EnqueueLocalMonitorFrame(pcm);

		long now = Environment.TickCount64;
		bool vad = false;
		if (ClientConfig.InputMode == "voiceActivation")
		{
			if (rms >= ClientConfig.VoiceActivationThreshold) VadUntilMS = now + ClientConfig.VoiceActivationHangoverMS;
			vad = now <= VadUntilMS;
		}

		bool transmit = ClientConfig.InputMode switch
		{
			"openMic" => true,
			"voiceActivation" => vad,
			_ => pttOrOpen
		};

		if (!transmit) { OutgoingEffects.ResetForSilence(); return; }

		VoiceOutgoingEffectState effects = OutgoingEffectState;
		if (OutgoingEffects.NeedsProcess(ClientConfig.InputGain, effects))
		{
			EnsureOutgoingEffectScratch(pcm.Length);
			LogOutgoingEffectProcessing(effects);
			OutgoingEffects.ProcessInPlace(pcm, OutgoingEffectScratch, ClientConfig.InputGain, effects);
		}
		else if (LastLoggedOutgoingProcessMask != 0)
		{
			LastLoggedOutgoingProcessMask = 0;
			ClientAPI.Logger.Notification("[YHPVC] Outgoing voice effect processing inactive.");
		}

		byte[] encoded = Encoder.Encode(pcm);
		if (encoded.Length == 0) return;

		if (VoiceBinaryProtocol.ClientPayloadSize(encoded.Length) > MaxPayloadBytes) return;

		byte flags = (byte)(1 << 1);
		if (Encoder.FECEnabled) flags |= YHPVCConstants.FlagFecCapable;
		if (ClientConfig.InputMode == "pushToTalk") flags |= (byte)(1 << 2);

		Volatile.Write(ref LastTransmittedMs, Environment.TickCount64);

		OnFrame(new ClientVoiceFrame
		(
			Sequence++,
			FrameDurationMS == 40 ? YHPVCConstants.FrameDuration40Ms : YHPVCConstants.FrameDuration20Ms,
			flags,
			VoiceMath.ComputeAudioLevel(pcm),
			encoded
		));
	}


	private void EnsureOutgoingEffectScratch(int sampleCount)
	{
		if (sampleCount <= 0 || OutgoingEffectScratch.Length >= sampleCount) return;

		int oldLength = OutgoingEffectScratch.Length;
		OutgoingEffectScratch = new float[sampleCount];

		if (!OutgoingEffectScratchFailureLogged)
		{
			OutgoingEffectScratchFailureLogged = true;
			ClientAPI.Logger.Warning
			(
				"[YHPVC] Recovered missing outgoing voice effect scratch buffer: {0} -> {1} samples. Sender-side immersive effects would have been bypassed.",
				oldLength,
				sampleCount
			);
		}
	}

	private void LogOutgoingEffectProcessing(VoiceOutgoingEffectState effects)
	{
		byte mask = 0;
		if (Math.Abs(ClientConfig.InputGain - 1f) > 0.0001f) mask |= 1;
		if (effects.HasMuffle) mask |= 2;
		if (effects.HasReverb) mask |= 4;
		if (effects.HasWarp) mask |= 8;

		if (mask == LastLoggedOutgoingProcessMask) return;
		LastLoggedOutgoingProcessMask = mask;

		ClientAPI.Logger.Notification
		(
			"[YHPVC] Outgoing voice effect processing active: inputGain={0:0.00}, muffle={1:0.00}, reverb={2:0.00}, drunkWarp={3:0.00}, scratch={4} samples.",
			ClientConfig.InputGain,
			effects.UnderwaterMuffle,
			effects.CaveReverb,
			effects.DrunkWarp,
			OutgoingEffectScratch.Length
		);
	}


	public void Stop() { ClientAPI.Event.EnqueueMainThreadTask(() => StopMainThread(), "yhpvc-stop-capture"); }

	private void StopMainThread(bool clearOutgoingEffects = true)
	{
		try
		{
			if (CaptureDevice != ALCaptureDevice.Null)
			{
				ALC.CaptureStop(CaptureDevice);
				ALC.CaptureCloseDevice(CaptureDevice);
			}
		}
		catch (Exception ex) { ClientAPI.Logger.Warning("[YHPVC] Capture stop failed: {0}", ex); }

		CaptureDevice = ALCaptureDevice.Null;

		try { Denoiser?.Dispose(); } catch { }
		Denoiser = null;
		RawCaptureResampler = null;

		try { Encoder?.Dispose(); } catch { }
		Encoder = null;
		FrameFill = 0;
		CaptureScratch = Array.Empty<short>();
		CaptureFrameScratch = Array.Empty<short>();
		NetworkFrameScratch = Array.Empty<short>();
		Raw48Scratch = Array.Empty<float>();
		OutgoingEffectScratch = Array.Empty<float>();
		LocalMonitorScratch = Array.Empty<short>();
		OutgoingEffects.ResetForSilence();
		LastLoggedOutgoingProcessMask = 0;
		if (clearOutgoingEffects) { OutgoingEffectState = VoiceOutgoingEffectState.Disabled; }
		Waveform.Clear();
		Volatile.Write(ref LastTransmittedMs, 0);
	}

	public void Dispose()
	{
		Disposed = true;
		Stop();
	}
}
