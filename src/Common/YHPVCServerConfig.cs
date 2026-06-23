using System;

namespace YHPVC.Common;

public sealed class YHPVCServerConfig
{
	public int AudibleRangeBlocks { get; set; } = 48;
	public float FullVolumeDistanceBlocks { get; set; } = 2f; // Distance where gain remains full before linear falloff begins
	public int RoutePaddingBlocks { get; set; } = 16;
	public int GridCellSizeBlocks { get; set; } = 64;
	public int ServerGridRepairIntervalMS { get; set; } = 2000;

	public int MaxVoicePayloadBytes { get; set; } = 430;
	public int BundleFlushIntervalMs { get; set; } = 20;

	public int SampleRate { get; set; } = 16000;
	public int FrameDurationMS { get; set; } = 20;
	public int MinBitrateKBPS { get; set; } = 4;
	public int MaxBitrateKBPS { get; set; } = 24;
	public int DefaultBitrateKBPS { get; set; } = 20;
	public int OpusComplexity { get; set; } = 2;
	public bool DTX { get; set; } = true;
	public bool AdaptiveFEC { get; set; } = true;
	public bool DynamicBitrate { get; set; } = true;

	// Client-side immersive voice effects. Speaker-owned effects are applied before encode. Listener-owned effects are applied after receive mix.
	public bool ImmersiveEffects { get; set; } = true;

	// FEC is useful against packet loss but it costs bandwidth. These thresholds are intentionally conservative so dense meetings prefer bitrate savings.
	public int FECLossEnablePercent { get; set; } = 8;
	public int FECLossDisablePercent { get; set; } = 3;
	public int FECDisableAboveFanout { get; set; } = 64;

	// Limits codec-control packet spam from fanout/FEC changes.
	public int CodecControlMinIntervalMS { get; set; } = 1000;
	public int CodecQualityRaiseIntervalMS { get; set; } = 5000;

	public bool DebugLogging { get; set; } = false;

	public int ServerRouteRadiusBlocks => AudibleRangeBlocks + RoutePaddingBlocks;

	public void Sanitize()
	{
		AudibleRangeBlocks = Math.Clamp(AudibleRangeBlocks, 4, 512);
		FullVolumeDistanceBlocks = Math.Clamp(FullVolumeDistanceBlocks, 0f, Math.Max(0f, AudibleRangeBlocks - 0.01f));
		RoutePaddingBlocks = Math.Clamp(RoutePaddingBlocks, 0, 512);

		if (GridCellSizeBlocks < 8) GridCellSizeBlocks = 8;
		GridCellSizeBlocks = NextPowerOfTwo(GridCellSizeBlocks);

		ServerGridRepairIntervalMS = Math.Clamp(ServerGridRepairIntervalMS, 250, 30000);
		MaxVoicePayloadBytes = Math.Clamp(MaxVoicePayloadBytes, 128, 480);
		BundleFlushIntervalMs = Math.Clamp(BundleFlushIntervalMs, 10, 100);

		SampleRate = SampleRate switch { 8000 or 12000 or 16000 or 24000 or 48000 => SampleRate, _ => 16000 };
		FrameDurationMS = FrameDurationMS == 40 ? 40 : 20;

		MinBitrateKBPS = Math.Clamp(MinBitrateKBPS, 4, 128);
		MaxBitrateKBPS = Math.Clamp(MaxBitrateKBPS, MinBitrateKBPS, 256);
		DefaultBitrateKBPS = Math.Clamp(DefaultBitrateKBPS, MinBitrateKBPS, MaxBitrateKBPS);
		OpusComplexity = Math.Clamp(OpusComplexity, 0, 10);

		FECLossDisablePercent = Math.Clamp(FECLossDisablePercent, 0, 50);
		FECLossEnablePercent = Math.Clamp(FECLossEnablePercent, FECLossDisablePercent, 60);
		FECDisableAboveFanout = Math.Clamp(FECDisableAboveFanout, 0, 512);

		CodecControlMinIntervalMS = Math.Clamp(CodecControlMinIntervalMS, 250, 10000);
		CodecQualityRaiseIntervalMS = Math.Clamp(Math.Max(CodecQualityRaiseIntervalMS, CodecControlMinIntervalMS), CodecControlMinIntervalMS, 60000);
	}

	private static int NextPowerOfTwo(int value)
	{
		int p = 1;
		while (p < value) p <<= 1;
		return p;
	}
}
