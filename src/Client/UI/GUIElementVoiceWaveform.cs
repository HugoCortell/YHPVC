using System;
using Cairo;
using Vintagestory.API.Client;

namespace YHPVC.Client;

internal sealed class VoiceWaveformTextureRenderer : IDisposable
{
	private const int RedrawIntervalMs = 50;

	private readonly ICoreClientAPI ClientAPI;
	private readonly VoiceWaveformBuffer WaveForm;
	private readonly byte[] VisualLevels;
	private LoadedTexture Texture;

	private ImageSurface? Surface;
	private Context? Context;
	private long LastRedrawMs;
	private bool Disposed;

	public VoiceWaveformTextureRenderer(ICoreClientAPI api, VoiceWaveformBuffer waveform)
	{
		this.ClientAPI = api;
		this.WaveForm = waveform;
		VisualLevels = new byte[waveform.Capacity];
		Texture = new LoadedTexture(api);
	}

	public void Render(ElementBounds bounds, float z)
	{
		if (Disposed) return;
		bounds.CalcWorldBounds();

		bool resized = EnsureSurface(Math.Max(1, bounds.OuterWidthInt), Math.Max(1, bounds.OuterHeightInt));
		Redraw(force: resized);

		if (Texture.TextureId != 0) { ClientAPI.Render.Render2DTexturePremultipliedAlpha(Texture.TextureId, bounds, z); }
	}

	public void Render(float x, float y, float width, float height, float z)
	{
		if (Disposed) return;

		bool resized = EnsureSurface(Math.Max(1, (int)MathF.Ceiling(width)), Math.Max(1, (int)MathF.Ceiling(height)));
		Redraw(force: resized);

		if (Texture.TextureId != 0) { ClientAPI.Render.Render2DTexturePremultipliedAlpha(Texture.TextureId, x, y, width, height, z); }
	}

	private bool EnsureSurface(int width, int height)
	{
		if (Surface != null && Surface.Width == width && Surface.Height == height) return false;

		Context?.Dispose();
		Surface?.Dispose();

		Surface = new ImageSurface(Format.Argb32, width, height);
		Context = new Context(Surface) { Antialias = Antialias.None };

		return true;
	}

	private void Redraw(bool force)
	{
		long now = Environment.TickCount64;
		if (!force && now - LastRedrawMs < RedrawIntervalMs) return;
		if (Surface == null || Context == null) return;

		WaveForm.CopyLatest(VisualLevels);

		long lastInputMs = WaveForm.LastUpdateMS;
		float staleMultiplier = 1f;
		if (lastInputMs <= 0) { staleMultiplier = 0f; }
		else
		{
			long ageMs = now - lastInputMs;
			if (ageMs > 500) { staleMultiplier = Math.Clamp(1f - ((ageMs - 500) / 500f), 0f, 1f); }
		}

		DrawWaveform(Context, Surface.Width, Surface.Height, staleMultiplier);
		ClientAPI.Gui.LoadOrUpdateCairoTexture(Surface, true, ref Texture);
		LastRedrawMs = now;
	}

	private void DrawWaveform(Context ctx, int width, int height, float staleMultiplier)
	{
		ctx.Save();

		ctx.Operator = Operator.Clear;
		ctx.Paint();
		ctx.Operator = Operator.Over;

		double centerY = height / 2.0;
		double maxAmplitude = Math.Max(1, (height - 4) / 2.0);
		double step = width / (double)VisualLevels.Length;
		double barWidth = Math.Max(1, step - 1);

		ctx.SetSourceRGBA(1, 1, 1, 0.20);
		ctx.LineWidth = 1;
		ctx.MoveTo(0, centerY + 0.5);
		ctx.LineTo(width, centerY + 0.5);
		ctx.Stroke();

		ctx.SetSourceRGBA(0.56, 0.82, 1.0, 0.88);

		for (int i = 0; i < VisualLevels.Length; i++)
		{
			double level = (VisualLevels[i] / 255.0) * staleMultiplier;
			if (level <= 0.001) continue;

			double amplitude = Math.Max(1, level * maxAmplitude);
			double x = i * step;
			double y = centerY - amplitude;

			ctx.Rectangle(x, y, barWidth, amplitude * 2);
			ctx.Fill();
		}

		ctx.Restore();
	}

	public void Dispose()
	{
		Disposed = true;
		Context?.Dispose(); Context = null;
		Surface?.Dispose(); Surface = null;
		Texture.Dispose();
	}
}

internal sealed class GuiElementVoiceWaveform : GuiElement
{
	private readonly VoiceWaveformTextureRenderer renderer;

	public GuiElementVoiceWaveform(ICoreClientAPI capi, ElementBounds bounds, VoiceWaveformBuffer waveform) : base(capi, bounds)
	{
		renderer = new VoiceWaveformTextureRenderer(capi, waveform);
	}

	public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
	{
		Bounds.CalcWorldBounds();
	}

	public override void RenderInteractiveElements(float deltaTime)
	{
		renderer.Render(Bounds, 51);
	}

	public override void Dispose()
	{
		base.Dispose();
		renderer.Dispose();
	}
}

internal static class GuiComposerVoiceWaveformExtensions
{
	public static GuiComposer AddYhpvcVoiceWaveform(this GuiComposer composer, VoiceWaveformBuffer waveform, ElementBounds bounds, string? key = null)
	{
		if (!composer.Composed) { composer.AddInteractiveElement(new GuiElementVoiceWaveform(composer.Api, bounds, waveform), key); }
		return composer;
	}
}
