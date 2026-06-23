namespace YHPVC.Common;

public static class YHPVCConstants
{
	public const string ModID = "yhpvc";
	public const string ControlChannel = "yhpvc-control";
	public const string VoiceChannel = "yhpvc-voice";

	public const string ServerConfigFile = "yhpvc-server.json";
	public const string ClientConfigFile = "yhpvc-client.json";

	public const byte FrameDuration20Ms = 0;
	public const byte FrameDuration40Ms = 1;

	// Set by the sender when the Opus packet was encoded with in-band FEC enabled.
	public const byte FlagFecCapable = 1 << 0;
	public const string PushToTalkHotkeyCode = "yhpvc-ptt";
	public const string MuteHotkeyCode = "yhpvc-mute";

	public const string MicIdleIconPath = "textures/icons/mic_idle.svg";
	public const string MicMutedIconPath = "textures/icons/mic_muted.svg";
	public const string MicTalkingIconPath = "textures/icons/mic_talking.svg";
}
