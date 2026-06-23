using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using YHPVC.Common;

namespace YHPVC.Client.Debug;

internal sealed class VoiceAttenuationDebugRenderer : IRenderer
{
	private static readonly int[] RingRadii = { 1, 2, 4, 8, 16, 32, 64, 128, 256 }; // Too much space at further out reaches
	private const int MinSpeakerLineActiveMs = 300;
	private const float SpeakerLineHeightOffset = 1.6f;

	private readonly ICoreClientAPI ClientAPI;
	private readonly YHPVCClientConfig ClientConfig;
	private readonly Func<ServerHelloMsg?> GetHelloFunct;
	private readonly Func<VoiceSoftwareMixer?> GetMixerFunct;
	private readonly List<VoiceDebugSpeakerLine> SpeakerLines = new(16);

	public VoiceAttenuationDebugRenderer(ICoreClientAPI api, YHPVCClientConfig cfg, Func<ServerHelloMsg?> getHello, Func<VoiceSoftwareMixer?> getMixer)
	{
		this.ClientAPI = api;
		this.ClientConfig = cfg;
		this.GetHelloFunct = getHello;
		this.GetMixerFunct = getMixer;
	}

	public double RenderOrder => 0.51;
	public int RenderRange => 9999;

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		ServerHelloMsg? hello = GetHelloFunct();						if (hello == null) return;
		EntityPlayer? localEntity = ClientAPI.World.Player?.Entity;		if (localEntity?.Pos == null) return;
		EntityPlayer target = GetTargetPlayer(localEntity);				if (target.Pos == null || target.Pos.Dimension != localEntity.Pos.Dimension) return;

		double audibleRange = Math.Max(0.001, hello.AudibleRangeBlocks);
		float fullVolumeDistance = Math.Clamp(hello.FullVolumeDistanceBlocks, 0f, Math.Max(0f, (float)audibleRange - 0.01f));

		DrawRings(target, fullVolumeDistance, audibleRange);
		DrawActiveSpeakerLines();
	}

	private EntityPlayer GetTargetPlayer(EntityPlayer localEntity)
	{
		EntityPlayer? selectedPlayer = ClientAPI.World.Player?.CurrentEntitySelection?.Entity as EntityPlayer;
		if (selectedPlayer?.Pos != null && selectedPlayer.PlayerUID != localEntity.PlayerUID) { return selectedPlayer; }

		return localEntity;
	}

	private void DrawRings(EntityPlayer target, float fullVolumeDistance, double audibleRange)
	{
		var pos = target.Pos;
		int originX = VoiceMath.FloorBlockCoord(pos.X);
		int originY = VoiceMath.FloorBlockCoord(pos.Y + pos.DimensionYAdjustment);
		int originZ = VoiceMath.FloorBlockCoord(pos.Z);
		BlockPos origin = new(originX, originY, originZ, pos.Dimension);

		float centerX = (float)(pos.X - originX);
		float centerY = (float)(pos.Y + pos.DimensionYAdjustment - originY + 0.05);
		float centerZ = (float)(pos.Z - originZ);

		for (int i = 0; i < RingRadii.Length; i++)
		{
			int radius = RingRadii[i];
			int color = ColorForRadius(radius, fullVolumeDistance, audibleRange);
			DrawOctagon(origin, centerX, centerY, centerZ, radius, color);
		}
	}

	private void DrawOctagon(BlockPos origin, float centerX, float centerY, float centerZ, float radius, int color)
	{
		const int segments = 8;
		const double step = Math.PI * 2.0 / segments;

		float firstX = centerX + radius;
		float firstZ = centerZ;
		float previousX = firstX;
		float previousZ = firstZ;

		for (int i = 1; i <= segments; i++)
		{
			double angle = i * step;
			float nextX = centerX + (float)(Math.Cos(angle) * radius);
			float nextZ = centerZ + (float)(Math.Sin(angle) * radius);

			ClientAPI.Render.RenderLine(origin, previousX, centerY, previousZ, nextX, centerY, nextZ, color);

			previousX = nextX;
			previousZ = nextZ;
		}
	}

	private void DrawActiveSpeakerLines()
	{
		VoiceSoftwareMixer? mixer = GetMixerFunct();
		if (mixer == null) return;

		SpeakerLines.Clear();

		long nowMs = Environment.TickCount64;
		int activeMs = Math.Max(MinSpeakerLineActiveMs, ClientConfig.JitterMaxMS + 100);

		mixer.CollectDebugSpeakerLines(SpeakerLines, nowMs, activeMs);
		for (int i = 0; i < SpeakerLines.Count; i++) { DrawSpeakerLine(SpeakerLines[i]); }

		SpeakerLines.Clear();
	}

	private void DrawSpeakerLine(VoiceDebugSpeakerLine line)
	{
		int originX = VoiceMath.FloorBlockCoord(line.ListenerX);
		int originY = VoiceMath.FloorBlockCoord(line.ListenerY);
		int originZ = VoiceMath.FloorBlockCoord(line.ListenerZ);
		BlockPos origin = new(originX, originY, originZ, line.Dimension);

		float sx = (float)(line.SpeakerX - originX);
		float sy = (float)(line.SpeakerY - originY + SpeakerLineHeightOffset);
		float sz = (float)(line.SpeakerZ - originZ);

		float lx = (float)(line.ListenerX - originX);
		float ly = (float)(line.ListenerY - originY + SpeakerLineHeightOffset);
		float lz = (float)(line.ListenerZ - originZ);

		ClientAPI.Render.RenderLine(origin, sx, sy, sz, lx, ly, lz, ColorForGain(line.Gain, 240));
	}

	private static int ColorForRadius(int radius, float fullVolumeDistance, double audibleRange)
	{
		float gain = VoiceMath.DistanceGainWithFullVolumeZone(radius * radius, fullVolumeDistance, audibleRange);
		return ColorForGain(gain, 220);
	}

	private static int ColorForGain(float gain, int alpha)
	{
		gain = Math.Clamp(gain, 0f, 1f);

		int red = (int)Math.Round(255 * (1f - gain));
		int green = (int)Math.Round(255 * gain);

		return ToLineColor(alpha, red, green, 0);
	}

	// The API's function for colour is somehow incorrectly written so it needs a conversion
	private static int ToLineColor(int alpha, int red, int green, int blue) { return ColorUtil.ToRgba(alpha, blue, green, red); }

	public void Dispose()
	{
		SpeakerLines.Clear();
	}
}
