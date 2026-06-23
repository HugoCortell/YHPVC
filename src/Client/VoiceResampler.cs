using System;

namespace YHPVC.Client;

internal sealed class VoiceResampler
{
	private const int FirTapCount = 47;

	private readonly int Factor;
	private readonly float[] Coefficients;
	private readonly float[] History;

	public int InputRate { get; }
	public int OutputRate { get; }

	public VoiceResampler(int inputRate, int outputRate)
	{
		if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
		if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
		if (inputRate % outputRate != 0) throw new ArgumentException("Input rate must be an integer multiple of output rate.");
		if (inputRate != RNNoiseDenoiser.RNNoiseSampleRate) throw new ArgumentException("VoiceResampler currently expects 48 kHz input.");
		if (outputRate is not (8000 or 12000 or 16000 or 24000 or 48000)) throw new ArgumentOutOfRangeException(nameof(outputRate));

		InputRate = inputRate;
		OutputRate = outputRate;
		Factor = inputRate / outputRate;

		Coefficients = Factor == 1 ? Array.Empty<float>() : BuildLowPassCoefficients(Factor);
		History = Factor == 1 ? Array.Empty<float>() : new float[FirTapCount - 1];
	}

	public void Process48ToNetwork(ReadOnlySpan<float> input48, Span<short> output)
	{
		int expectedOutput = input48.Length / Factor;
		if (input48.Length != expectedOutput * Factor) throw new ArgumentException("Input length must be divisible by the resample factor.", nameof(input48));
		if (output.Length < expectedOutput) throw new ArgumentException("Output buffer is too small.", nameof(output));

		if (Factor == 1)
		{
			for (int i = 0; i < expectedOutput; i++) { output[i] = FloatToPcm16(input48[i]); }
			return;
		}

		for (int outIndex = 0; outIndex < expectedOutput; outIndex++)
		{
			int sourceIndex = outIndex * Factor;
			float sample = 0f;

			for (int tap = 0; tap < FirTapCount; tap++)
			{
				int inputIndex = sourceIndex - tap;
				float x = inputIndex >= 0 ? input48[inputIndex] : History[History.Length + inputIndex];

				sample += x * Coefficients[tap];
			}

			output[outIndex] = FloatToPcm16(sample);
		}

		SaveHistory(input48);
	}

	public void Reset() { if (History.Length > 0) Array.Clear(History, 0, History.Length); }

	private void SaveHistory(ReadOnlySpan<float> input)
	{
		if (History.Length == 0) return;

		if (input.Length >= History.Length)
		{
			input.Slice(input.Length - History.Length, History.Length).CopyTo(History);
			return;
		}

		int keep = History.Length - input.Length;
		Array.Copy(History, History.Length - keep, History, 0, keep);

		for (int i = 0; i < input.Length; i++) { History[keep + i] = input[i]; }
	}

	private static float[] BuildLowPassCoefficients(int factor)
	{
		var coefficients = new float[FirTapCount];

		// Cut slightly below the target Nyquist to leave transition-band room before decimation
		double cutoff = 0.45 / factor;
		int center = FirTapCount / 2;
		double sum = 0.0;

		for (int i = 0; i < FirTapCount; i++)
		{
			int n = i - center;

			double sinc = n == 0 ? 2.0 * cutoff : Math.Sin(2.0 * Math.PI * cutoff * n) / (Math.PI * n);

			double window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (FirTapCount - 1));
			double h = sinc * window;

			coefficients[i] = (float)h;
			sum += h;
		}

		if (sum == 0.0) return coefficients;
		for (int i = 0; i < coefficients.Length; i++) { coefficients[i] = (float)(coefficients[i] / sum); }

		return coefficients;
	}

	private static short FloatToPcm16(float sample)
	{
		if (sample > short.MaxValue) return short.MaxValue;
		if (sample < short.MinValue) return short.MinValue;
		return (short)MathF.Round(sample);
	}
}
