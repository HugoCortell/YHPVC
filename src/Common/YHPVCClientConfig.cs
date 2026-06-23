using System;

namespace YHPVC.Common;

public sealed class YHPVCClientConfig
{
	public string InputMode { get; set; } = "voiceActivation"; // "pushToTalk", "voiceActivation", or "openMic"

	public float IncomingGain { get; set; } = 1f;
	public float InputGain { get; set; } = 1f;
	public bool HearSelf { get; set; } = false;
	public bool NoiseSuppressionEnabled { get; set; } = true;
	public float NoiseSuppressionMix { get; set; } = 1f;

	public int ClientCellCheckIntervalMS { get; set; } = 100;

	public int JitterTargetMS { get; set; } = 60;
	public int JitterMaxMS { get; set; } = 120;

	public float VoiceActivationThreshold { get; set; } = 0.015f;
	public int VoiceActivationHangoverMS { get; set; } = 250;

	public string InputDevice { get; set; } = "";
	public string PreferredOutputDevice { get; set; } = "";

	// Software 3D audio. This keeps the scalable single-OpenAL-source model while still giving each speaker directionality.
	public float StereoPanStrength { get; set; } = 0.85f;
	public bool InvertStereoPan { get; set; } = false;

	public bool ShowStatusIcon { get; set; } = true;
	public bool FirstBootConfigShown { get; set; } = false;

	public void Sanitize()
	{
		InputMode = InputMode switch
		{
			"pushToTalk" or "voiceActivation" or "openMic" => InputMode,
			_ => "voiceActivation"
		};

		IncomingGain = Math.Clamp(IncomingGain, 0f, 1.5f);
		InputGain = Math.Clamp(InputGain, 0f, 4f);
		NoiseSuppressionMix = Math.Clamp(NoiseSuppressionMix, 0f, 1f);

		ClientCellCheckIntervalMS = Math.Clamp(ClientCellCheckIntervalMS, 50, 1000);

		JitterTargetMS = Math.Clamp(JitterTargetMS, 20, 250);
		JitterMaxMS = Math.Clamp(Math.Max(JitterMaxMS, JitterTargetMS), JitterTargetMS, 1000);

		VoiceActivationThreshold = Math.Clamp(VoiceActivationThreshold, 0.001f, 1f);
		VoiceActivationHangoverMS = Math.Clamp(VoiceActivationHangoverMS, 0, 5000);

		StereoPanStrength = Math.Clamp(StereoPanStrength, 0f, 1f);
	}
}
