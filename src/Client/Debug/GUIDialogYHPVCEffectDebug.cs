using System;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using YHPVC.Client;

namespace YHPVC.Client.Debug;

internal sealed class GUIDialogYHPVCEffectDebug : GuiDialog
{
	private const string DrunkKey = "Drunk";
	private const string PsychedelicKey = "Psychedelic";
	private const string CaveKey = "Cave";
	private const string SourceUnderwaterKey = "SourceUnderwater";
	private const string ListenerUnderwaterKey = "ListenerUnderwater";
	private const string TemporalKey = "Temporal";

	private const int DrunkSliderMax = 220;
	private const int PsychedelicSliderMax = 400;
	private const int CaveSliderMax = 70;
	private const int TemporalSliderMax = 100;

	private readonly Action<VoiceEffectPreviewSettings> PlayPreview;

	private float DrunkIntoxication;
	private float PsychedelicHigh;
	private float CaveReverbness;
	private bool SourceSubmerged;
	private bool ListenerSubmerged;
	private float TemporalGlitchStrength;
	private VoiceEffectPreviewSourceLocation Source;
	private bool HasSource;

	public override string ToggleKeyCombinationCode => null!;
	public override double DrawOrder => 0.2;
	public override bool PrefersUngrabbedMouse => true;

	public GUIDialogYHPVCEffectDebug(ICoreClientAPI capi, Action<VoiceEffectPreviewSettings> playPreview) : base(capi) { this.PlayPreview = playPreview; }

	public override bool TryOpen()
	{
		if (!HasSource) { TrySetSourceHere(showMessage: false); }

		ComposeDialog();
		return base.TryOpen();
	}

	private void ComposeDialog()
	{
		const double width = 500;
		const double panelPad = 16;
		const double labelWidth = 182;
		const double sliderX = panelPad + labelWidth + 10;
		const double sliderWidth = width - panelPad * 2 - labelWidth - 10;

		ElementBounds titleBounds = ElementBounds.Fixed(panelPad, 18, width - panelPad * 2, 24);
		ElementBounds panelBounds = ElementBounds.Fixed(0, 56, width, 264);

		ElementBounds drunkLabelBounds = ElementBounds.Fixed(panelPad, 74, labelWidth, 22);
		ElementBounds drunkBounds = ElementBounds.Fixed(sliderX, 76, sliderWidth, 20);

		ElementBounds psychedelicLabelBounds = ElementBounds.Fixed(panelPad, 112, labelWidth, 22);
		ElementBounds psychedelicBounds = ElementBounds.Fixed(sliderX, 114, sliderWidth, 20);

		ElementBounds caveLabelBounds = ElementBounds.Fixed(panelPad, 150, labelWidth, 22);
		ElementBounds caveBounds = ElementBounds.Fixed(sliderX, 152, sliderWidth, 20);

		ElementBounds sourceWaterLabelBounds = ElementBounds.Fixed(panelPad, 188, labelWidth, 22);
		ElementBounds sourceWaterBounds = ElementBounds.Fixed(sliderX, 190, sliderWidth, 20);

		ElementBounds listenerWaterLabelBounds = ElementBounds.Fixed(panelPad, 226, labelWidth, 22);
		ElementBounds listenerWaterBounds = ElementBounds.Fixed(sliderX, 228, sliderWidth, 20);

		ElementBounds temporalLabelBounds = ElementBounds.Fixed(panelPad, 264, labelWidth, 22);
		ElementBounds temporalBounds = ElementBounds.Fixed(sliderX, 266, sliderWidth, 20);

		ElementBounds setSourceButtonBounds = ElementBounds.Fixed(panelPad, 340, 212, 32);
		ElementBounds playButtonBounds = ElementBounds.Fixed(width - panelPad - 212, 340, 212, 32);

		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		backgroundBounds.BothSizing = ElementSizing.FitToChildren;
		backgroundBounds.WithChildren(titleBounds, panelBounds, setSourceButtonBounds, playButtonBounds);

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

		SingleComposer?.Dispose();
		SingleComposer = capi.Gui.CreateCompo("yhpvc-effect-debug", dialogBounds)
			.AddShadedDialogBG(backgroundBounds, withTitleBar: false)
			.BeginChildElements(backgroundBounds)
				.AddStaticText("YHPVC Effect Preview", CairoFont.WhiteSmallText().WithFontSize(18), titleBounds)
				.AddInset(panelBounds, depth: 4, brightness: 0.85f)

				.AddStaticText("Drunk intoxication:", CairoFont.WhiteSmallText(), drunkLabelBounds)
				.AddSlider(OnDrunkChanged, drunkBounds, DrunkKey)

				.AddStaticText("Psychedelic:", CairoFont.WhiteSmallText(), psychedelicLabelBounds)
				.AddSlider(OnPsychedelicChanged, psychedelicBounds, PsychedelicKey)

				.AddStaticText("Cave reverbness:", CairoFont.WhiteSmallText(), caveLabelBounds)
				.AddSlider(OnCaveChanged, caveBounds, CaveKey)

				.AddStaticText("Source underwater:", CairoFont.WhiteSmallText(), sourceWaterLabelBounds)
				.AddSlider(OnSourceUnderwaterChanged, sourceWaterBounds, SourceUnderwaterKey)

				.AddStaticText("Listener underwater:", CairoFont.WhiteSmallText(), listenerWaterLabelBounds)
				.AddSlider(OnListenerUnderwaterChanged, listenerWaterBounds, ListenerUnderwaterKey)

				.AddStaticText("Temporal glitch strength:", CairoFont.WhiteSmallText(), temporalLabelBounds)
				.AddSlider(OnTemporalChanged, temporalBounds, TemporalKey)

				.AddSmallButton("Set Source Origin", OnSetSourceHereClicked, setSourceButtonBounds)
				.AddSmallButton("Play Random Audio", OnPlayClicked, playButtonBounds)
			.EndChildElements()
			.Compose();

		ConfigureDrunkSlider();
		ConfigurePsychedelicSlider();
		ConfigureCaveSlider();
		ConfigureBoolSlider(SourceUnderwaterKey, SourceSubmerged);
		ConfigureBoolSlider(ListenerUnderwaterKey, ListenerSubmerged);
		ConfigureTemporalSlider();
	}

	private void ConfigureDrunkSlider()
	{
		GuiElementSlider slider = SingleComposer.GetSlider(DrunkKey);
		slider.OnSliderTooltip = FormatDrunk;
		slider.ShowTextWhenResting = false;
		slider.SetValues(ToDrunkSliderValue(DrunkIntoxication), 0, DrunkSliderMax, 1);
	}

	private void ConfigurePsychedelicSlider()
	{
		GuiElementSlider slider = SingleComposer.GetSlider(PsychedelicKey);
		slider.OnSliderTooltip = FormatPsychedelic;
		slider.ShowTextWhenResting = false;
		slider.SetValues(ToPsychedelicSliderValue(PsychedelicHigh), 0, PsychedelicSliderMax, 1);
	}

	private void ConfigureCaveSlider()
	{
		GuiElementSlider slider = SingleComposer.GetSlider(CaveKey);
		slider.OnSliderTooltip = FormatCave;
		slider.ShowTextWhenResting = false;
		slider.SetValues(ToCaveSliderValue(CaveReverbness), 0, CaveSliderMax, 1);
	}

	private void ConfigureBoolSlider(string key, bool value)
	{
		GuiElementSlider slider = SingleComposer.GetSlider(key);
		slider.OnSliderTooltip = FormatBool;
		slider.ShowTextWhenResting = false;
		slider.SetValues(value ? 1 : 0, 0, 1, 1);
	}

	private void ConfigureTemporalSlider()
	{
		GuiElementSlider slider = SingleComposer.GetSlider(TemporalKey);
		slider.OnSliderTooltip = FormatTemporal;
		slider.ShowTextWhenResting = false;
		slider.SetValues(ToTemporalSliderValue(TemporalGlitchStrength), 0, TemporalSliderMax, 1);
	}

	private bool OnDrunkChanged(int value) 					{ DrunkIntoxication = FromDrunkSliderValue(value); return true; }
	private bool OnPsychedelicChanged(int value) 			{ PsychedelicHigh = FromPsychedelicSliderValue(value); return true; }
	private bool OnCaveChanged(int value) 					{ CaveReverbness = FromCaveSliderValue(value); return true; }
	private bool OnSourceUnderwaterChanged(int value) 		{ SourceSubmerged = value != 0; return true; }
	private bool OnListenerUnderwaterChanged(int value) 	{ ListenerSubmerged = value != 0; return true; }
	private bool OnTemporalChanged(int value) 				{ TemporalGlitchStrength = FromTemporalSliderValue(value); return true; }
	private bool OnSetSourceHereClicked() 					{ TrySetSourceHere(showMessage: true); return true; }

	private bool OnPlayClicked()
	{
		try
		{
			if (!HasSource) { TrySetSourceHere(showMessage: false); }

			PlayPreview(new VoiceEffectPreviewSettings
			(
				Source,
				DrunkIntoxication,
				PsychedelicHigh,
				CaveReverbness,
				SourceSubmerged,
				ListenerSubmerged,
				TemporalGlitchStrength)
			);

			capi.ShowChatMessage("[YHPVC] Playing random effect preview clip.");
		}
		catch (Exception ex)
		{
			capi.Logger.Warning("[YHPVC] Effect preview failed: {0}", ex);
			capi.ShowChatMessage("[YHPVC] Effect preview failed: " + ex.Message);
		}

		return true;
	}

	private bool TrySetSourceHere(bool showMessage)
	{
		var pos = capi.World.Player?.Entity?.Pos;
		if (pos == null) return false;

		Source = new VoiceEffectPreviewSourceLocation
		(
			pos.X,
			pos.Y + pos.DimensionYAdjustment,
			pos.Z,
			pos.Dimension
		);

		HasSource = true;

		if (showMessage) { capi.ShowChatMessage("[YHPVC] Audio origin point set!"); }
		return true;
	}

	private static int ToDrunkSliderValue(float value)
	{
		return Math.Clamp((int)MathF.Round(value / VoiceImmersiveEffectMapping.DrunkIntoxicationStep), 0, DrunkSliderMax);
	}

	private static float FromDrunkSliderValue(int value)
	{
		return Math.Clamp(value, 0, DrunkSliderMax) * VoiceImmersiveEffectMapping.DrunkIntoxicationStep;
	}

	private static int ToPsychedelicSliderValue(float value)
	{
		return Math.Clamp((int)MathF.Round(value / VoiceImmersiveEffectMapping.PsychedelicStep), 0, PsychedelicSliderMax);
	}

	private static float FromPsychedelicSliderValue(int value)
	{
		return Math.Clamp(value, 0, PsychedelicSliderMax) * VoiceImmersiveEffectMapping.PsychedelicStep;
	}

	private static int ToCaveSliderValue(float value)
	{
		return Math.Clamp((int)MathF.Round(value / VoiceImmersiveEffectMapping.CaveReverbnessStep), 0, CaveSliderMax);
	}

	private static float FromCaveSliderValue(int value)
	{
		return Math.Clamp(value, 0, CaveSliderMax) * VoiceImmersiveEffectMapping.CaveReverbnessStep;
	}

	private static int ToTemporalSliderValue(float value)
	{
		return Math.Clamp((int)MathF.Round(value * 100f), 0, TemporalSliderMax);
	}

	private static float FromTemporalSliderValue(int value)
	{
		return Math.Clamp(value, 0, TemporalSliderMax) / 100f;
	}

	private static string FormatDrunk(int value)
	{
		float intox = FromDrunkSliderValue(value);
		float voiceWarp = VoiceImmersiveEffectMapping.DrunkWarpFromIntoxication(intox);
		return intox.ToString("0.000", CultureInfo.InvariantCulture) + " intox, voice warp " + voiceWarp.ToString("0.00", CultureInfo.InvariantCulture);
	}

	private static string FormatPsychedelic(int value)
	{
		float psyche = FromPsychedelicSliderValue(value);
		float voiceWarp = VoiceImmersiveEffectMapping.PsychedelicWarpFromPsychedelic(psyche);
		return psyche.ToString("0.000", CultureInfo.InvariantCulture) + " psychedelic, voice warp " + voiceWarp.ToString("0.00", CultureInfo.InvariantCulture);
	}

	private static string FormatCave(int value)
	{
		float reverbness = FromCaveSliderValue(value);
		float voiceReverb = VoiceImmersiveEffectMapping.CaveReverbFromReverbness(reverbness, submerged: false);
		return reverbness.ToString("0.0", CultureInfo.InvariantCulture) + " reverbness, voice reverb " + voiceReverb.ToString("0.00", CultureInfo.InvariantCulture);
	}

	private static string FormatBool(int value)
	{
		return value == 0 ? "0 / false" : "1 / true";
	}

	private static string FormatTemporal(int value)
	{
		float glitch = FromTemporalSliderValue(value);
		float voiceWarp = VoiceImmersiveEffectMapping.TemporalWarpFromGlitchStrength(glitch);
		return glitch.ToString("0.00", CultureInfo.InvariantCulture) + " glitch strength, voice warp " + voiceWarp.ToString("0.00", CultureInfo.InvariantCulture);
	}
}
