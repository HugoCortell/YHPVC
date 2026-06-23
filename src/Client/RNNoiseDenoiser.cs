using System;
using YHPVC.Common;

namespace YHPVC.Client;

internal sealed class RNNoiseDenoiser : IDisposable
{
	public const int RNNoiseSampleRate = 48000;
	public const int RNNoiseFrameSamples = 480;

	private readonly float[] RNNoiseInput = new float[RNNoiseFrameSamples];
	private readonly float[] RNNoiseOutput = new float[RNNoiseFrameSamples];
	private readonly float[] Denoised48;
	private readonly VoiceResampler Downsampler;
	private readonly float Mix;

	private IntPtr State;

	public int NetworkSampleRate { get; }
	public int FrameDurationMS { get; }
	public int CaptureFrameSamples { get; }
	public int NetworkFrameSamples { get; }

	public RNNoiseDenoiser(int networkSampleRate, int frameDurationMs, float mix)
	{
		NetworkSampleRate = networkSampleRate;
		FrameDurationMS = frameDurationMs;
		CaptureFrameSamples = VoiceMath.FrameSamples(RNNoiseSampleRate, frameDurationMs);
		NetworkFrameSamples = VoiceMath.FrameSamples(networkSampleRate, frameDurationMs);
		this.Mix = Math.Clamp(mix, 0f, 1f);

		if (CaptureFrameSamples % RNNoiseFrameSamples != 0)
		{
			throw new ArgumentException($"RNNoise capture frame size must be a multiple of {RNNoiseFrameSamples} samples.", nameof(frameDurationMs));
		}

		State = RNNoiseNative.rnnoise_create(IntPtr.Zero);
		if (State == IntPtr.Zero) throw new InvalidOperationException("[YHPVC] RNNoise_create returned null.");

		Denoised48 = new float[CaptureFrameSamples];
		Downsampler = new VoiceResampler(RNNoiseSampleRate, networkSampleRate);
	}

	public bool ProcessFrame(ReadOnlySpan<short> capturePcm48, Span<short> outputNetworkPcm)
	{
		if (State == IntPtr.Zero) return false;
		if (capturePcm48.Length < CaptureFrameSamples) return false;
		if (outputNetworkPcm.Length < NetworkFrameSamples) return false;

		try
		{
			for (int offset = 0; offset < CaptureFrameSamples; offset += RNNoiseFrameSamples)
			{
				for (int i = 0; i < RNNoiseFrameSamples; i++) { RNNoiseInput[i] = capturePcm48[offset + i]; }

				RNNoiseNative.rnnoise_process_frame(State, RNNoiseOutput, RNNoiseInput);

				for (int i = 0; i < RNNoiseFrameSamples; i++)
				{
					float wet = RNNoiseOutput[i];
					if (float.IsNaN(wet) || float.IsInfinity(wet)) wet = 0f;

					float dry = capturePcm48[offset + i];
					Denoised48[offset + i] = dry + (wet - dry) * Mix;
				}
			}

			Downsampler.Process48ToNetwork(Denoised48, outputNetworkPcm.Slice(0, NetworkFrameSamples));
			return true;
		}
		catch { return false; }
	}

	public void Reset() { Downsampler.Reset(); }

	public void Dispose()
	{
		IntPtr local = State;
		State = IntPtr.Zero;

		if (local != IntPtr.Zero) { RNNoiseNative.rnnoise_destroy(local); }
	}
}
