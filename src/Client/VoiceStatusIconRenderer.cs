using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using YHPVC.Common;

namespace YHPVC.Client;

internal enum VoiceHudStatus
{
	Muted = 0,
	Idle = 1,
	Talking = 2
}

internal sealed class VoiceStatusIconRenderer : IRenderer
{
	// IRenderer docs: Ortho order 1.0 is GUI manager, 1.02 is crosshair/mouse cursor. Draw after normal GUI but before the mouse cursor/crosshair layer.
	private const float HudZ = 1000f;

	private static readonly Vec4f ShadowColor = new(0f, 0f, 0f, 0.72f);
	private static readonly Vec4f MutedColor = new(0.82f, 0.82f, 0.82f, 1f);
	private static readonly Vec4f ActiveColor = new(0.93f, 0.93f, 0.93f, 1f);

	private readonly ICoreClientAPI ClientAPI;
	private readonly YHPVCClientConfig ClientConfig;
	private readonly Func<VoiceHudStatus> GetStatusFunct;
	private readonly Func<bool> ShouldShowWaveformFunct;
	private readonly VoiceWaveformTextureRenderer WaveformRenderer;

	private LoadedTexture? IdleTexture;
	private LoadedTexture? MutedTexture;
	private LoadedTexture? TalkingTexture;

	private int LoadedSize;
	private bool LoadAttempted;
	private bool LoggedMissingAssets;
	private bool Disposed;

	public VoiceStatusIconRenderer
	(
		ICoreClientAPI api, YHPVCClientConfig cfg, VoiceWaveformBuffer waveform,
		Func<VoiceHudStatus> getStatus, Func<bool> shouldShowWaveform
	)
	{
		this.ClientAPI = api;
		this.ClientConfig = cfg;
		this.GetStatusFunct = getStatus;
		this.ShouldShowWaveformFunct = shouldShowWaveform;
		WaveformRenderer = new VoiceWaveformTextureRenderer(api, waveform);
	}

	public double RenderOrder => 1.01;
	public int RenderRange => 0;

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		if (Disposed || !ClientConfig.ShowStatusIcon || stage != EnumRenderStage.Ortho) return;

		int size = GuiElement.scaledi(64); // Icon size
		int margin = GuiElement.scaledi(16); // Icon margins
		bool renderWaveform = ShouldShowWaveformFunct();

		EnsureTextures(size);

		VoiceHudStatus status = GetStatusFunct();
		LoadedTexture? texture = status switch
		{
			VoiceHudStatus.Talking => TalkingTexture,
			VoiceHudStatus.Muted => MutedTexture,
			_ => IdleTexture
		};

		if (texture == null || texture.TextureId == 0 || texture.Disposed) return;

		float x;
		float y;

		if (renderWaveform)
		{
			float waveformWidth = GuiElement.scaledi(128);
			float waveformHeight = GuiElement.scaledi(28);
			float waveformGap = GuiElement.scaledi(4);

			float waveformX = Math.Max(0, ClientAPI.Render.FrameWidth - waveformWidth - margin);
			float waveformY = Math.Max(0, ClientAPI.Render.FrameHeight - waveformHeight - margin);

			x = Math.Max(0, waveformX + (waveformWidth - size) * 0.5f);
			y = Math.Max(0, waveformY - waveformGap - size);

			WaveformRenderer.Render(waveformX, waveformY, waveformWidth, waveformHeight, HudZ);
		}
		else
		{
			x = Math.Max(0, ClientAPI.Render.FrameWidth - size - margin);
			y = Math.Max(0, ClientAPI.Render.FrameHeight - size - margin);
		}

		Vec4f mainColor = status == VoiceHudStatus.Muted ? MutedColor : ActiveColor;

		float shadowOffset = Math.Max(1f, (float)GuiElement.scaled(2.0));
		float shadowInflate = Math.Max(2f, (float)GuiElement.scaled(4.0));

		// VS SVG textures are generated through the games Cairo/SvgLoader path and rendered like other GUI textures with Render2DTexture().
		// Render a tinted shadow first so the black SVG source art remains visible against both bright and dark backgrounds.
		ClientAPI.Render.Render2DTexture
		(
			texture.TextureId,
			x + shadowOffset - shadowInflate * 0.5f, y + shadowOffset - shadowInflate * 0.5f,
			size + shadowInflate, size + shadowInflate,
			HudZ - 1f,
			ShadowColor
		);

		ClientAPI.Render.Render2DTexture(texture.TextureId, x, y, size, size, HudZ, mainColor);
	}

	private void EnsureTextures(int size)
	{
		if (LoadAttempted && LoadedSize == size) return;

		DisposeTextures();
		LoadedSize = size;
		LoadAttempted = true;
		LoggedMissingAssets = false;

		try
		{
			IdleTexture = LoadIcon(YHPVCConstants.MicIdleIconPath, size);
			MutedTexture = LoadIcon(YHPVCConstants.MicMutedIconPath, size);
			TalkingTexture = LoadIcon(YHPVCConstants.MicTalkingIconPath, size);

			if (IdleTexture == null || MutedTexture == null || TalkingTexture == null) { LogMissingAssetsOnce(); }
		}
		catch (Exception ex)
		{
			ClientAPI.Logger.Warning("[YHPVC] Failed to load voice status icon textures: {0}", ex);
			DisposeTextures();
		}
	}

	private LoadedTexture? LoadIcon(string path, int size)
	{
		var loc = new AssetLocation(YHPVCConstants.ModID, path);
		LoadedTexture? texture = ClientAPI.Gui.LoadSvgWithPadding(loc, size, size, padding: 0, color: unchecked((int)0xFFFFFFFF));

		if (texture == null || texture.TextureId == 0)
		{
			ClientAPI.Logger.Warning("[YHPVC] Voice status icon asset could not be loaded: {0}", loc);
			texture?.Dispose();

			return null;
		}

		return texture;
	}

	private void LogMissingAssetsOnce()
	{
		if (LoggedMissingAssets) return;
		LoggedMissingAssets = true;

		ClientAPI.Logger.Warning
		(
			"[YHPVC] One or more voice status icons are missing. Expected assets: {0}, {1}, {2}",
			new AssetLocation(YHPVCConstants.ModID, YHPVCConstants.MicIdleIconPath),
			new AssetLocation(YHPVCConstants.ModID, YHPVCConstants.MicMutedIconPath),
			new AssetLocation(YHPVCConstants.ModID, YHPVCConstants.MicTalkingIconPath)
		);
	}

	private void DisposeTextures()
	{
		try { IdleTexture?.Dispose(); }			catch { }
		try { MutedTexture?.Dispose(); }		catch { }
		try { TalkingTexture?.Dispose(); }		catch { }

		IdleTexture = null;
		MutedTexture = null;
		TalkingTexture = null;
	}

	public void Dispose()
	{
		Disposed = true;
		DisposeTextures();
		WaveformRenderer.Dispose();
	}
}
