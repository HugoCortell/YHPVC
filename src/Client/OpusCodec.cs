using System;
using OpusSharp.Core;
using YHPVC.Common;

namespace YHPVC.Client;

internal sealed class OpusEncoderAdapter : IDisposable
{
	private readonly OpusEncoder Encoder;
	private readonly byte[] EncodedScratch = new byte[512];

	public int SampleRate { get; }
	public int FrameDurationMS { get; }
	public int FrameSamples { get; }
	public bool FECEnabled { get; private set; }
	public int PacketLossPercent { get; private set; }

	public OpusEncoderAdapter(int sampleRate, int frameDurationMs, int bitrateKbps, int complexity, bool dtx, bool fec)
	{
		SampleRate = sampleRate;
		FrameDurationMS = frameDurationMs;
		FrameSamples = VoiceMath.FrameSamples(sampleRate, frameDurationMs);

		Encoder = new OpusEncoder(sampleRate, 1, OpusPredefinedValues.OPUS_APPLICATION_VOIP);

		SetBitrateKbps(bitrateKbps);
		Encoder.Ctl(EncoderCTL.OPUS_SET_COMPLEXITY, complexity);
		Encoder.Ctl(EncoderCTL.OPUS_SET_DTX, dtx ? 1 : 0);
		Encoder.Ctl(EncoderCTL.OPUS_SET_VBR, 1);
		Encoder.Ctl(EncoderCTL.OPUS_SET_VBR_CONSTRAINT, 1);
		SetFec(fec, 0);
	}

	public void SetBitrateKbps(int kbps) { Encoder.Ctl(EncoderCTL.OPUS_SET_BITRATE, Math.Max(4000, kbps * 1000)); }

	public void SetFec(bool enabled, int packetLossPercent)
	{
		FECEnabled = enabled;
		PacketLossPercent = Math.Clamp(packetLossPercent, 0, 50);

		Encoder.Ctl(EncoderCTL.OPUS_SET_INBAND_FEC, enabled ? 1 : 0);
		Encoder.Ctl(EncoderCTL.OPUS_SET_PACKET_LOSS_PERC, PacketLossPercent);
	}

	public byte[] Encode(short[] pcm)
	{
		if (pcm.Length < FrameSamples) return Array.Empty<byte>();

		int encodedBytes = Encoder.Encode(pcm, FrameSamples, EncodedScratch, EncodedScratch.Length);
		if (encodedBytes <= 0) return Array.Empty<byte>();

		byte[] output = new byte[encodedBytes];
		Buffer.BlockCopy(EncodedScratch, 0, output, 0, encodedBytes);
		return output;
	}

	public void Dispose() => Encoder.Dispose();
}

internal sealed class OpusDecoderAdapter : IDisposable
{
	private readonly OpusDecoder Decoder;
	private readonly short[] PCMScratch;

	public int SampleRate { get; }
	public int FrameDurationMS { get; }
	public int FrameSamples { get; }

	public OpusDecoderAdapter(int sampleRate, int frameDurationMs)
	{
		SampleRate = sampleRate;
		FrameDurationMS = frameDurationMs;
		FrameSamples = VoiceMath.FrameSamples(sampleRate, frameDurationMs);
		PCMScratch = new short[FrameSamples];
		Decoder = new OpusDecoder(sampleRate, 1);
	}

	public ReadOnlySpan<short> DecodeToScratch(byte[] opus, bool decodeFec)
	{
		Array.Clear(PCMScratch, 0, PCMScratch.Length);

		try
		{
			int decoded = Decoder.Decode(opus, opus.Length, PCMScratch, FrameSamples, decodeFec);
			if (decoded <= 0) Array.Clear(PCMScratch, 0, PCMScratch.Length);
		}
		catch { Array.Clear(PCMScratch, 0, PCMScratch.Length); }

		return PCMScratch;
	}

	public ReadOnlySpan<short> DecodePlcToScratch()
	{
		Array.Clear(PCMScratch, 0, PCMScratch.Length);

		try
		{
			int decoded = Decoder.Decode(Array.Empty<byte>(), 0, PCMScratch, FrameSamples, false);
			if (decoded <= 0) Array.Clear(PCMScratch, 0, PCMScratch.Length);
		}
		catch { Array.Clear(PCMScratch, 0, PCMScratch.Length); }

		return PCMScratch;
	}

	public void Dispose() => Decoder.Dispose();
}
