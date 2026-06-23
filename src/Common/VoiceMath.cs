using System;

namespace YHPVC.Common;

public static class VoiceMath
{
	public static int FloorBlockCoord(double value) => (int)Math.Floor(value);

	public static int FloorDiv(int value, int divisor)
	{
		if (divisor <= 0) throw new ArgumentOutOfRangeException(nameof(divisor));
		int q = value / divisor;
		int r = value % divisor;
		return r != 0 && ((r < 0) != (divisor < 0)) ? q - 1 : q;
	}

	public static int CoordToCell(double coord, int cellSizeBlocks) => FloorDiv(FloorBlockCoord(coord), cellSizeBlocks);

	public static bool SequenceOlderThan(ushort a, ushort b)		=> (short)(a - b) < 0;
	public static bool SequenceNewerThan(ushort a, ushort b)		=> (short)(a - b) > 0;
	public static ushort SequenceAdd(ushort sequence, int delta)	=> unchecked((ushort)(sequence + delta));

	public static byte ComputeAudioLevel(ReadOnlySpan<short> pcm)
	{
		if (pcm.Length == 0) return 0;

		double sum = 0;
		for (int i = 0; i < pcm.Length; i++)
		{
			double v = pcm[i] / 32768.0;
			sum += v * v;
		}

		double rms = Math.Sqrt(sum / pcm.Length);
		return (byte)Math.Clamp((int)(rms * 255.0 * 4.0), 0, 255);
	}

	public static float ComputeRms(ReadOnlySpan<short> pcm)
	{
		if (pcm.Length == 0) return 0f;

		double sum = 0;
		for (int i = 0; i < pcm.Length; i++)
		{
			double v = pcm[i] / 32768.0;
			sum += v * v;
		}

		return (float)Math.Sqrt(sum / pcm.Length);
	}

	public static int FrameSamples(int sampleRate, int frameDurationMs) => sampleRate * frameDurationMs / 1000;

	public static float LinearDistanceGain(double distSq, double maxRange)
	{
		if (maxRange <= 0) return 0f;
		double maxSq = maxRange * maxRange;
		if (distSq >= maxSq) return 0f;

		double dist = Math.Sqrt(distSq);
		return (float)Math.Clamp(1.0 - dist / maxRange, 0.0, 1.0);
	}

	public static float DistanceGainWithFullVolumeZone(double distSq, float fullVolumeDistance, double maxRange)
	{
		if (maxRange <= 0) return 0f;
		double maxSq = maxRange * maxRange;
		if (distSq >= maxSq) return 0f;

		double dist = Math.Sqrt(distSq);
		if (dist <= fullVolumeDistance) return 1f;

		double falloffDistance = Math.Max(0.001, maxRange - fullVolumeDistance);
		return (float)Math.Clamp(1.0 - ((dist - fullVolumeDistance) / falloffDistance), 0.0, 1.0);
	}

	public static void EqualPowerPan(float pan, out float leftGain, out float rightGain)
	{
		pan = Math.Clamp(pan, -1f, 1f);
		leftGain = MathF.Sqrt(0.5f * (1f - pan));
		rightGain = MathF.Sqrt(0.5f * (1f + pan));
	}

	public static float SoftLimit(float sample) { return sample / (1f + Math.Abs(sample)); }

	public static short FloatToPcm16(float sample)
	{
		sample = Math.Clamp(sample, -1f, 1f);
		return (short)(sample * short.MaxValue);
	}
}
