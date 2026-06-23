using System;
using YHPVC.Common;

namespace YHPVC.Client;

public readonly struct VoiceSpatialContext
{
	public VoiceSpatialContext
	(
		ushort speakerPeerId,
		float gain,
		float leftGain,
		float rightGain,
		float pan,
		double distanceSquared)
	{
		SpeakerPeerID = speakerPeerId;
		Gain = gain;
		LeftGain = leftGain;
		RightGain = rightGain;
		Pan = pan;
		DistanceSquared = distanceSquared;
	}

	public ushort SpeakerPeerID { get; }
	public float Gain { get; }
	public float LeftGain { get; }
	public float RightGain { get; }
	public float Pan { get; }
	public double DistanceSquared { get; }
}

public interface IVoicePostProcessor
{
	// Mixes a mono decoded speaker frame into an interleaved stereo float mix buffer.
	// Effects such as underwater/cave/occlusion can be inserted here later without changing packet routing, jitter buffering, or OpenAL output.
	void ProcessSpeakerFrame
	(
		ushort speakerPeerId,
		ReadOnlySpan<short> inputMono,
		Span<float> interleavedStereoMixBuffer,
		VoiceSpatialContext context
	);
}

internal sealed class DefaultVoicePostProcessor : IVoicePostProcessor
{
	public void ProcessSpeakerFrame
	(
		ushort speakerPeerId,
		ReadOnlySpan<short> inputMono,
		Span<float> interleavedStereoMixBuffer,
		VoiceSpatialContext context
	)
	{
		float left = context.Gain * context.LeftGain;
		float right = context.Gain * context.RightGain;

		int monoSamples = Math.Min(inputMono.Length, interleavedStereoMixBuffer.Length / 2);
		for (int i = 0; i < monoSamples; i++)
		{
			float sample = inputMono[i] / 32768f;
			int o = i * 2;
			interleavedStereoMixBuffer[o] += sample * left;
			interleavedStereoMixBuffer[o + 1] += sample * right;
		}
	}
}

internal static class VoiceSpatialMath
{
	public static float ComputePan(double dx, double dz, float listenerYaw)
	{
		double horizontalDist = Math.Sqrt(dx * dx + dz * dz);
		if (horizontalDist < 0.001) return 0f;

		double nx = dx / horizontalDist;
		double nz = dz / horizontalDist;

		// Vintage Story yaw is radians. This right-vector convention matches the usual X/Z horizontal plane
		// and keeps panning in software so we can use a single scalable OpenAL stream instead of one source per speaker.
		double rightX = Math.Cos(listenerYaw);
		double rightZ = Math.Sin(listenerYaw);

		return (float)Math.Clamp(nx * rightX + nz * rightZ, -1.0, 1.0);
	}
}

