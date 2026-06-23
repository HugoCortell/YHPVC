using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using YHPVC.Common;

namespace YHPVC.Client;

internal sealed class GuiDialogYHPVCConfig : GuiDialog
{
	private const string InputGainKey = "InputGain";
	private const string IncomingGainKey = "IncomingGain";
	private const string HearSelfKey = "HearSelf";
	private const string ShowInGameKey = "ShowInGame";
	private const string NoiseSuppressionKey = "NoiseSuppression";
	private const string VoiceActivationThresholdKey = "VoiceActivationThreshold";
	private const string KeybindHintKey = "KeybindHint";

	private static readonly string[] InputModeValues =
	{
		"pushToTalk",
		"voiceActivation",
		"openMic"
	};

	private static readonly string[] NoiseSuppressionValues =
	{
		"off",
		"half",
		"full"
	};


	private readonly YHPVCClientConfig ClientConfig;
	private readonly YHPVCClientSystem ClientSystem;

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.2;
	public override bool PrefersUngrabbedMouse => true;

	public GuiDialogYHPVCConfig(ICoreClientAPI capi, YHPVCClientConfig cfg, YHPVCClientSystem clientSystem) : base(capi)
	{
		this.ClientConfig = cfg;
		this.ClientSystem = clientSystem;
	}

	public override bool TryOpen()
	{
		ComposeDialog();
		return base.TryOpen();
	}

	private void ComposeDialog()
	{
		NormalizeUiRangeValues();
		CollectInputDevices(out string[] deviceValues, out string[] deviceNames);

		int selectedInputModeIndex = GetSelectedInputModeIndex();
		int selectedDeviceIndex = GetSelectedDeviceIndex(deviceValues);
		int selectedNoiseSuppressionIndex = GetSelectedNoiseSuppressionIndex();
		bool showVoiceActivationThreshold = string.Equals(InputModeValues[selectedInputModeIndex], "voiceActivation", StringComparison.Ordinal);
		string[] inputModeNames = GetInputModeNames();
		string[] noiseSuppressionNames = GetNoiseSuppressionNames();

		const double width = 520;
		const double panelPad = 16;

		ElementBounds bannerBounds = ElementBounds.Fixed(20, 0, 480, 120);

		ElementBounds waveformPanelBounds = ElementBounds.Fixed(0, 136, width, 116);
		ElementBounds waveformLabelBounds = ElementBounds.Fixed(panelPad, 148, width - panelPad * 2, 22);
		ElementBounds waveformBounds = ElementBounds.Fixed(panelPad, 176, width - panelPad * 2, 34);
		ElementBounds showInGameLabelBounds = ElementBounds.Fixed(panelPad, 224, 126, 18);
		ElementBounds showInGameSwitchBounds = ElementBounds.Fixed(panelPad + 132, 222, 18, 18);
		ElementBounds hearSelfLabelBounds = ElementBounds.Fixed(218, 224, 240, 18);
		ElementBounds hearSelfSwitchBounds = ElementBounds.Fixed(466, 222, 18, 18);

		const double dropdownLabelWidth = 144;
		const double dropdownGap = 8;
		double dropdownX = panelPad + dropdownLabelWidth + dropdownGap;
		double dropdownWidth = width - panelPad * 2 - dropdownLabelWidth - dropdownGap;

		const double activationThresholdHeight = 34;
		double activationThresholdOffset = showVoiceActivationThreshold ? activationThresholdHeight : 0;

		ElementBounds capturePanelBounds = ElementBounds.Fixed(0, 268, width, 184 + activationThresholdOffset);
		ElementBounds inputModeLabelBounds = ElementBounds.Fixed(panelPad, 286, dropdownLabelWidth, 22);
		ElementBounds inputModeBounds = ElementBounds.Fixed(dropdownX, 278, dropdownWidth, 30);
		ElementBounds thresholdLabelBounds = ElementBounds.Fixed(panelPad, 324, 82, 22);
		ElementBounds thresholdBounds = ElementBounds.Fixed(panelPad + 88, 324, width - panelPad * 2 - 88, 20);
		ElementBounds keybindHintBounds = ElementBounds.Fixed(panelPad, 322 + activationThresholdOffset, width - panelPad * 2, 40);
		ElementBounds microphoneLabelBounds = ElementBounds.Fixed(panelPad, 374 + activationThresholdOffset, dropdownLabelWidth, 22);
		ElementBounds microphoneBounds = ElementBounds.Fixed(dropdownX, 366 + activationThresholdOffset, dropdownWidth, 30);
		ElementBounds noiseSuppressionLabelBounds = ElementBounds.Fixed(panelPad, 416 + activationThresholdOffset, dropdownLabelWidth, 22);
		ElementBounds noiseSuppressionBounds = ElementBounds.Fixed(dropdownX, 408 + activationThresholdOffset, dropdownWidth, 30);

		ElementBounds gainPanelBounds = ElementBounds.Fixed(0, 468 + activationThresholdOffset, width, 148);
		ElementBounds inputGainLabelBounds = ElementBounds.Fixed(panelPad, 482 + activationThresholdOffset, width - panelPad * 2, 22);
		ElementBounds inputGainBounds = ElementBounds.Fixed(panelPad, 508 + activationThresholdOffset, width - panelPad * 2, 20);
		ElementBounds incomingGainLabelBounds = ElementBounds.Fixed(panelPad, 552 + activationThresholdOffset, width - panelPad * 2, 22);
		ElementBounds incomingGainBounds = ElementBounds.Fixed(panelPad, 578 + activationThresholdOffset, width - panelPad * 2, 20);

		ElementBounds closeButtonBounds = ElementBounds.Fixed(180, 634 + activationThresholdOffset, 160, 32);

		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		backgroundBounds.BothSizing = ElementSizing.FitToChildren;
		backgroundBounds.WithChildren(bannerBounds, closeButtonBounds);

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

		SingleComposer?.Dispose();
		SingleComposer = capi.Gui.CreateCompo("yhpvc-config", dialogBounds)
			.AddShadedDialogBG(backgroundBounds, withTitleBar: false)
			.BeginChildElements(backgroundBounds)
				.AddImage(bannerBounds, new AssetLocation("yhpvc:textures/yhpvc_banner.png"))

				.AddInset(waveformPanelBounds, depth: 4, brightness: 0.85f)
				.AddStaticText(Lang.Get("yhpvc:config-microphone-preview"), CairoFont.WhiteSmallText(), waveformLabelBounds)
				.AddYhpvcVoiceWaveform(ClientSystem.Waveform, waveformBounds, "MicWaveform")
				.AddStaticText(Lang.Get("yhpvc:config-show-in-game"), CairoFont.WhiteDetailText().WithFontSize(14), showInGameLabelBounds)
				.AddSwitch(OnShowInGameToggled, showInGameSwitchBounds, ShowInGameKey, size: 20, padding: 2)
				.AddStaticText(Lang.Get("yhpvc:config-enable-loopback"), CairoFont.WhiteDetailText().WithFontSize(14), hearSelfLabelBounds)
				.AddSwitch(OnHearSelfToggled, hearSelfSwitchBounds, HearSelfKey, size: 20, padding: 2)

				.AddInset(capturePanelBounds, depth: 4, brightness: 0.85f)
				.AddStaticText(Lang.Get("yhpvc:config-capture-mode"), CairoFont.WhiteSmallText(), inputModeLabelBounds)
				.AddDropDown(InputModeValues, inputModeNames, selectedInputModeIndex, OnInputModeChanged, inputModeBounds, "InputMode")
				.AddIf(showVoiceActivationThreshold)
					.AddStaticText("Threshold:", CairoFont.WhiteSmallText(), thresholdLabelBounds)
					.AddSlider(OnVoiceActivationThresholdChanged, thresholdBounds, VoiceActivationThresholdKey)
				.EndIf()
				.AddRichtext(BuildKeybindHintVtml(), CairoFont.WhiteDetailText(), keybindHintBounds, KeybindHintKey)
				.AddStaticText(Lang.Get("yhpvc:config-microphone"), CairoFont.WhiteSmallText(), microphoneLabelBounds)
				.AddDropDown(deviceValues, deviceNames, selectedDeviceIndex, OnInputDeviceChanged, microphoneBounds, "InputDevice")
				.AddStaticText(Lang.Get("yhpvc:config-noise-suppression"), CairoFont.WhiteSmallText(), noiseSuppressionLabelBounds)
				.AddDropDown
				(
					NoiseSuppressionValues, noiseSuppressionNames, selectedNoiseSuppressionIndex,
					OnNoiseSuppressionChanged, noiseSuppressionBounds, NoiseSuppressionKey
				)

				.AddInset(gainPanelBounds, depth: 4, brightness: 0.85f)
				.AddStaticText(Lang.Get("yhpvc:config-outgoing-voice-multiplier"), CairoFont.WhiteSmallText(), inputGainLabelBounds)
				.AddSlider(OnInputGainChanged, inputGainBounds, InputGainKey)
				.AddStaticText(Lang.Get("yhpvc:config-incoming-voice-multiplier"), CairoFont.WhiteSmallText(), incomingGainLabelBounds)
				.AddSlider(OnIncomingGainChanged, incomingGainBounds, IncomingGainKey)

				.AddSmallButton(Lang.Get("yhpvc:config-close-window"), OnCloseButton, closeButtonBounds)
			.EndChildElements()
			.Compose();

		SingleComposer.GetSwitch(ShowInGameKey).SetValue(ClientSystem.ShowWaveformInGame);
		SingleComposer.GetSwitch(HearSelfKey).SetValue(ClientConfig.HearSelf);

		if (showVoiceActivationThreshold) { ConfigureVoiceActivationThresholdSlider(); }

		ConfigureGainSlider(InputGainKey, ClientConfig.InputGain, min: 1, max: 40);
		ConfigureGainSlider(IncomingGainKey, ClientConfig.IncomingGain, min: 1, max: 15);
	}

	private void NormalizeUiRangeValues()
	{
		float inputGain = Math.Clamp(ClientConfig.InputGain, 0.1f, 4.0f);
		float incomingGain = Math.Clamp(ClientConfig.IncomingGain, 0.1f, 1.5f);
		float voiceActivationThreshold = Math.Clamp(ClientConfig.VoiceActivationThreshold, 0.005f, 0.05f);

		if 
		(
			Math.Abs(ClientConfig.InputGain - inputGain) < 0.0001f &&
			Math.Abs(ClientConfig.IncomingGain - incomingGain) < 0.0001f &&
			Math.Abs(ClientConfig.VoiceActivationThreshold - voiceActivationThreshold) < 0.0001f
		) { return; }

		ClientConfig.InputGain = inputGain;
		ClientConfig.IncomingGain = incomingGain;
		ClientConfig.VoiceActivationThreshold = voiceActivationThreshold;
		ClientSystem.SaveClientConfig();
	}

	private int GetSelectedInputModeIndex()
	{
		for (int i = 0; i < InputModeValues.Length; i++) { if (string.Equals(InputModeValues[i], ClientConfig.InputMode, StringComparison.Ordinal)) return i; }

		ClientConfig.InputMode = "voiceActivation";
		ClientSystem.SaveClientConfig();
		return 1;
	}

	private int GetSelectedDeviceIndex(string[] deviceValues)
	{
		string selectedDevice = ClientConfig.InputDevice ?? "";
		for (int i = 0; i < deviceValues.Length; i++) { if (string.Equals(deviceValues[i], selectedDevice, StringComparison.Ordinal)) return i; }

		if (!string.IsNullOrWhiteSpace(selectedDevice))
		{
			ClientConfig.InputDevice = "";
			ClientSystem.SaveClientConfig();
		}

		return 0;
	}

	private int GetSelectedNoiseSuppressionIndex()
	{
		if (!ClientConfig.NoiseSuppressionEnabled || ClientConfig.NoiseSuppressionMix <= 0.001f) return 0;
		return ClientConfig.NoiseSuppressionMix <= 0.75f ? 1 : 2;
	}

	private void ConfigureGainSlider(string key, float value, int min, int max)
	{
		GuiElementSlider slider = SingleComposer.GetSlider(key);
		slider.OnSliderTooltip = FormatGain;
		slider.ShowTextWhenResting = false;
		slider.SetValues(FloatGainToSliderValue(value, min, max), min, max, 1);
	}

	private void ConfigureVoiceActivationThresholdSlider()
	{
		GuiElementSlider slider = SingleComposer.GetSlider(VoiceActivationThresholdKey);
		slider.OnSliderTooltip = FormatVoiceActivationThreshold;
		slider.ShowTextWhenResting = false;
		slider.SetValues(VoiceActivationThresholdToSliderValue(ClientConfig.VoiceActivationThreshold), 5, 50, 5);
	}

	private void OnInputModeChanged(string code, bool selected)
	{
		if (!selected) return;

		code = IsValidInputMode(code) ? code : "voiceActivation";
		if (string.Equals(ClientConfig.InputMode, code, StringComparison.Ordinal)) return;

		ClientConfig.InputMode = code;
		ClientSystem.SaveClientConfig();
		UpdateKeybindHint();
		capi.Event.EnqueueMainThreadTask(() =>
		{
			if (IsOpened()) ComposeDialog();
		}, "yhpvc-refresh-config-input-mode");
	}

	private void OnInputDeviceChanged(string code, bool selected)
	{
		if (!selected) return;

		code ??= "";
		if (string.Equals(ClientConfig.InputDevice, code, StringComparison.Ordinal)) return;

		ClientConfig.InputDevice = code;
		ClientSystem.SaveClientConfig();
		ClientSystem.RestartCaptureIfRunning();
	}

	private void OnNoiseSuppressionChanged(string code, bool selected)
	{
		if (!selected) return;

		bool enabled;
		float mix;

		switch (code)
		{
			case "half":
				enabled = true;
				mix = 0.5f;
			break;

			case "full":
				enabled = true;
				mix = 1f;
			break;

			default:
				enabled = false;
				mix = 0f;
			break;
		}

		if (ClientConfig.NoiseSuppressionEnabled == enabled && Math.Abs(ClientConfig.NoiseSuppressionMix - mix) < 0.0001f) return;

		ClientConfig.NoiseSuppressionEnabled = enabled;
		ClientConfig.NoiseSuppressionMix = mix;
		ClientSystem.SaveClientConfig();
		ClientSystem.RestartCaptureIfRunning();
	}

	private bool OnInputGainChanged(int value)
	{
		float gain = SliderValueToFloatGain(value);
		if (Math.Abs(ClientConfig.InputGain - gain) < 0.0001f) return true;

		ClientConfig.InputGain = gain;
		ClientSystem.SaveClientConfig();
		return true;
	}

	private bool OnIncomingGainChanged(int value)
	{
		float gain = SliderValueToFloatGain(value);
		if (Math.Abs(ClientConfig.IncomingGain - gain) < 0.0001f) return true;

		ClientConfig.IncomingGain = gain;
		ClientSystem.SaveClientConfig();
		return true;
	}

	private bool OnVoiceActivationThresholdChanged(int value)
	{
		float threshold = SliderValueToVoiceActivationThreshold(value);
		if (Math.Abs(ClientConfig.VoiceActivationThreshold - threshold) < 0.0001f) return true;

		ClientConfig.VoiceActivationThreshold = threshold;
		ClientSystem.SaveClientConfig();
		return true;
	}

	private void OnShowInGameToggled(bool on)
	{
		ClientSystem.ShowWaveformInGame = on;
	}

	private void OnHearSelfToggled(bool on)
	{
		if (ClientConfig.HearSelf == on) return;

		ClientConfig.HearSelf = on;
		ClientSystem.SaveClientConfig();
	}

	private bool OnCloseButton()
	{
		TryClose();
		return true;
	}

	private void UpdateKeybindHint() { SingleComposer.GetRichtext(KeybindHintKey).SetNewText(BuildKeybindHintVtml(), CairoFont.WhiteDetailText()); }

	private string BuildKeybindHintVtml()
	{
		bool pushToTalk = string.Equals(ClientConfig.InputMode, "pushToTalk", StringComparison.Ordinal);
		string hotkeyCode = pushToTalk ? YHPVCConstants.PushToTalkHotkeyCode : YHPVCConstants.MuteHotkeyCode;
		string hotkeyName = pushToTalk ? Lang.Get("yhpvc:hotkey-push-to-talk") : Lang.Get("yhpvc:hotkey-mute");
		string keybindLine = pushToTalk ? Lang.Get("yhpvc:config-push-to-talk-keybind", hotkeyCode) : Lang.Get("yhpvc:config-mute-keybind", hotkeyCode);

		return keybindLine + "<br>" + Lang.Get("yhpvc:config-rebind-info", hotkeyName);
	}

	private static bool IsValidInputMode(string? code)
	{
		for (int i = 0; i < InputModeValues.Length; i++) { if (string.Equals(InputModeValues[i], code, StringComparison.Ordinal)) return true; }

		return false;
	}

	private static string[] GetInputModeNames()
	{
		return new[]
		{
			Lang.Get("yhpvc:config-input-mode-push-to-talk"),
			Lang.Get("yhpvc:config-input-mode-voice-activation"),
			Lang.Get("yhpvc:config-input-mode-open-microphone")
		};
	}

	private static string[] GetNoiseSuppressionNames()
	{
		return new[]
		{
			Lang.Get("yhpvc:config-noise-suppression-off"),
			Lang.Get("yhpvc:config-noise-suppression-half"),
			Lang.Get("yhpvc:config-noise-suppression-full")
		};
	}

	private static int FloatGainToSliderValue(float value, int min, int max)	{ return Math.Clamp((int)MathF.Round(value * 10f), min, max); }
	private static float SliderValueToFloatGain(int value)						{ return value / 10f; }
	private static int VoiceActivationThresholdToSliderValue(float value)		{ return Math.Clamp((int)MathF.Round(value * 1000f / 5f) * 5, 5, 50); }
	private static float SliderValueToVoiceActivationThreshold(int value)		{ return value / 1000f; }
	private static string FormatGain(int value)									{ return (value * 10).ToString(CultureInfo.InvariantCulture) + "%"; }
	private static string FormatVoiceActivationThreshold(int value)
	{
		return (SliderValueToVoiceActivationThreshold(value) * 1000f).ToString("0", CultureInfo.InvariantCulture) + "%";
	}

	private static void CollectInputDevices(out string[] values, out string[] names)
	{
		List<string> devices = new();

		try
		{
			foreach (string device in ALC.GetStringList(GetEnumerationStringList.CaptureDeviceSpecifier))
			{
				if (string.IsNullOrWhiteSpace(device) || devices.Contains(device)) continue;
				devices.Add(device);
			}
		}
		catch { } // OpenAL capture enumeration is best-effort. The default device can still work.

		values = new string[devices.Count + 1];
		names = new string[devices.Count + 1];
		values[0] = "";
		names[0] = Lang.Get("yhpvc:config-default-microphone");

		for (int i = 0; i < devices.Count; i++)
		{
			values[i + 1] = devices[i];
			names[i + 1] = devices[i];
		}
	}
}
