using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using YHPVC.Client.Debug;
using YHPVC.Common;

namespace YHPVC.Client;

internal readonly struct QueuedServerVoiceFrame
{
	public QueuedServerVoiceFrame(ServerVoiceFrame frame, long receivedMs)
	{
		Frame = frame;
		ReceivedMS = receivedMs;
	}

	public ServerVoiceFrame Frame { get; }
	public long ReceivedMS { get; }
}

public sealed class YHPVCClientSystem : IDisposable
{
	private const int MaxInboundFramesPerAudioFrame = 512;
	private const int MaxInboundFramesPerPump = 4096;

	private readonly ICoreClientAPI ClientAPI;
	private readonly YHPVCClientConfig ClientConfig;
	private readonly VoicePeerTable PeerTable = new();
	private readonly ConcurrentQueue<QueuedServerVoiceFrame> InboundFrames = new();

	[ThreadStatic] private static List<ServerVoiceFrame>? TLSServerBundleFrames;

	private IClientNetworkChannel ControlChannel = null!;
	private IClientNetworkChannel VoiceChannel = null!;

	private VoiceCaptureService? Capture;
	private VoiceSoftwareMixer? Mixer;
	private OpenAlStreamingOutput? Output;
	private VoiceEnvironmentSampler? EnvironmentSampler;
	private VoiceStatusIconRenderer? StatusIcon;
	private VoiceAttenuationDebugRenderer? AttenuationDebug;
	private VoiceEffectPreviewPlayer? EffectPreview;
	private GUIDialogYHPVCEffectDebug? EffectDebugDialog;
	private GuiDialogYHPVCConfig? ConfigDialog;
	private bool Muted;

	public VoiceWaveformBuffer Waveform { get; } = new();
	public bool ShowWaveformInGame { get; set; }

	private ServerHelloMsg? Hello;
	private bool ReadySent;
	private long ReadyTickID;
	private long CellTickID;
	private long CaptureTickID;
	private long MixTickID;
	private long StatsTickID;

	private int CurrentCellX = int.MinValue;
	private int CurrentCellZ = int.MinValue;
	private int CurrentDimension = int.MinValue;

	private int AudioFrameMS = 20;
	private int AudioAccumulatedMS;
	private long AudioLastTickMS;
	private long AudioPlaybackClockMS;
	private int MaxMixFramesPerPump = 6;

	public YHPVCClientSystem(ICoreClientAPI api, YHPVCClientConfig cfg)
	{
		this.ClientAPI = api;
		this.ClientConfig = cfg;
	}

	public void Start()
	{
		RegisterClientCommand();
		RegisterHotkeys();

		ControlChannel = ClientAPI.Network
			.GetChannel(YHPVCConstants.ControlChannel)
			.SetMessageHandler<ServerHelloMsg>(OnServerHello)
			.SetMessageHandler<PeerTableMsg>(OnPeerTable)
			.SetMessageHandler<PeerJoinedMsg>(OnPeerJoined)
			.SetMessageHandler<PeerLeftMsg>(OnPeerLeft)
			.SetMessageHandler<CodecControlMsg>(OnCodecControl);

		VoiceChannel = ClientAPI.Network
			.GetUdpChannel(YHPVCConstants.VoiceChannel)
			.SetMessageHandler<VoiceUDPDatagram>(OnVoiceDatagram);

		StatusIcon = new VoiceStatusIconRenderer(ClientAPI, ClientConfig, Waveform, GetVoiceHudStatus, () => ShowWaveformInGame);
		ClientAPI.Event.RegisterRenderer(StatusIcon, EnumRenderStage.Ortho, "yhpvc-status-icon");

		ClientAPI.Event.PlayerEntitySpawn += OnPlayerEntitySpawn;
		ClientAPI.Event.LeaveWorld += OnLeaveWorld;

		ReadyTickID = ClientAPI.Event.RegisterGameTickListener(_ => TrySendReady(), 500);

		ClientAPI.Logger.Notification("[YHPVC] Client system started.");
	}

	private void RegisterClientCommand()
	{
		ClientAPI.ChatCommands
			.GetOrCreate("yhpvc")
			.WithDescription(Lang.Get("yhpvc:command-description"))
			.HandleWith(_ =>
			{
				OpenConfigDialog();
				return TextCommandResult.Success();
			})
			.BeginSubCommand("debug")
				.WithDescription("YHPVC debug views")
				.RequiresPrivilege(Privilege.controlserver)
				.BeginSubCommand("att")
					.WithDescription("Toggle attenuation debug rings")
					.HandleWith(_ => ToggleAttenuationDebug())
				.EndSubCommand()
				.BeginSubCommand("effect")
					.WithDescription("Open voice effect preview")
					.HandleWith(_ => OpenEffectDebugDialog())
				.EndSubCommand()
			.EndSubCommand();
	}

	private void RegisterHotkeys()
	{
		ClientAPI.Input.RegisterHotKey
		(
			YHPVCConstants.PushToTalkHotkeyCode,
			Lang.Get("yhpvc:hotkey-push-to-talk"),
			GlKeys.V,
			HotkeyType.CharacterControls
		);

		ClientAPI.Input.RegisterHotKey
		(
			YHPVCConstants.MuteHotkeyCode,
			Lang.Get("yhpvc:hotkey-mute"),
			GlKeys.N,
			HotkeyType.CharacterControls
		);

		ClientAPI.Input.SetHotKeyHandler(YHPVCConstants.MuteHotkeyCode, OnMuteHotkey);
	}

	private void OpenConfigDialog()
	{
		ConfigDialog ??= new GuiDialogYHPVCConfig(ClientAPI, ClientConfig, this);
		ConfigDialog.TryOpen();
	}

	private TextCommandResult OpenEffectDebugDialog()
	{
		EffectDebugDialog ??= new GUIDialogYHPVCEffectDebug(ClientAPI, PlayEffectPreview);
		EffectDebugDialog.TryOpen();
		return TextCommandResult.Success();
	}

	private void PlayEffectPreview(VoiceEffectPreviewSettings settings)
	{
		EffectPreview ??= new VoiceEffectPreviewPlayer(ClientAPI, ClientConfig, () => Hello);
		EffectPreview.Play(settings);
	}

	private void StopEffectPreview()
	{
		try { EffectPreview?.Dispose(); } catch { }
		EffectPreview = null;
	}

	private TextCommandResult ToggleAttenuationDebug()
	{
		if (AttenuationDebug != null)
		{
			DisableAttenuationDebug();
			return TextCommandResult.Success("YHPVC attenuation debug: off");
		}

		if (Hello == null) { return TextCommandResult.Error("YHPVC voice runtime is not ready yet. Race condition!"); }

		AttenuationDebug = new VoiceAttenuationDebugRenderer(ClientAPI, ClientConfig, () => Hello, () => Mixer);
		ClientAPI.Event.RegisterRenderer(AttenuationDebug, EnumRenderStage.Opaque, "yhpvc-debug-att");
		return TextCommandResult.Success("YHPVC attenuation debug: on");
	}

	private void DisableAttenuationDebug()
	{
		if (AttenuationDebug == null) return;

		try { ClientAPI.Event.UnregisterRenderer(AttenuationDebug, EnumRenderStage.Opaque); } catch { }
		try { AttenuationDebug.Dispose(); } catch { }
		AttenuationDebug = null;
	}

	public void SaveClientConfig()
	{
		ClientConfig.Sanitize();
		ClientAPI.StoreModConfig(ClientConfig, YHPVCConstants.ClientConfigFile);
	}

	public void RestartCaptureIfRunning()
	{
		if (Hello == null || Capture == null) return;
		try { Capture.Dispose(); } catch { }

		Capture = new VoiceCaptureService
		(
			ClientAPI,
			ClientConfig,
			Waveform,
			ShouldTransmitPushToTalk,
			IsMuted,
			SendVoiceFrame,
			EnqueueLocalMonitorFrame
		);
		Capture.Start(Hello);
		EnvironmentSampler?.SetCapture(Capture);
	}

	private void OnPlayerEntitySpawn(IClientPlayer spawnedPlayer)
	{
		if (spawnedPlayer.PlayerUID != ClientAPI.World.Player?.PlayerUID) return;

		TrySendReady();
		OpenFirstBootConfigIfNeeded();
	}

	private void OpenFirstBootConfigIfNeeded()
	{
		if (ClientConfig.FirstBootConfigShown) return;

		ClientConfig.FirstBootConfigShown = true;
		SaveClientConfig();

		ClientAPI.Event.RegisterCallback(_ => OpenConfigDialog(), 50);
	}

	private void TrySendReady()
	{
		if (ReadySent || Hello != null) return;
		if (ClientAPI.World.Player?.Entity?.Pos == null) return;
		if (ControlChannel == null || !ControlChannel.Connected) return;

		try
		{
			ControlChannel.SendPacket(new ClientReadyMsg());
			ReadySent = true;
		}
		catch (Exception ex)
		{
			ClientAPI.Logger.Warning("[YHPVC] ClientReady send failed: {0}", ex);
			ReadySent = false;
		}
	}

	private void OnServerHello(ServerHelloMsg msg)
	{
		if (ClientAPI.World.Player?.Entity?.Pos == null)
		{
			ClientAPI.Event.RegisterCallback(_ => OnServerHello(msg), 250);
			return;
		}

		StopRuntime();

		Hello = msg;
		PeerTable.LocalPeerID = msg.SelfPeerId;

		Mixer = new VoiceSoftwareMixer(ClientAPI, ClientConfig, PeerTable);
		Mixer.Configure(msg);

		Output = new OpenAlStreamingOutput(ClientAPI);
		Output.Start(msg.SampleRate, channels: 2);

		Capture = new VoiceCaptureService
		(
			ClientAPI,
			ClientConfig,
			Waveform,
			ShouldTransmitPushToTalk,
			IsMuted,
			SendVoiceFrame,
			EnqueueLocalMonitorFrame
		);
		Capture.Start(msg);

		if (msg.ImmersiveEffects)
		{
			EnvironmentSampler = new VoiceEnvironmentSampler(ClientAPI, Capture, Mixer);
			EnvironmentSampler.Start();
		}

		if (ReadyTickID != 0)
		{
			try { ClientAPI.Event.UnregisterGameTickListener(ReadyTickID); } catch { }
			ReadyTickID = 0;
		}

		AudioFrameMS = Math.Max(1, msg.FrameDurationMS);
		AudioAccumulatedMS = 0;
		AudioLastTickMS = Environment.TickCount64;
		AudioPlaybackClockMS = AudioLastTickMS;
		MaxMixFramesPerPump = Math.Max(2, 120 / AudioFrameMS);

		CellTickID = ClientAPI.Event.RegisterGameTickListener(_ => CheckCellAndSend(), Math.Max(50, ClientConfig.ClientCellCheckIntervalMS));
		CaptureTickID = ClientAPI.Event.RegisterGameTickListener(_ => Capture?.Tick(), Math.Max(5, msg.FrameDurationMS / 2));
		MixTickID = ClientAPI.Event.RegisterGameTickListener(_ => AudioPumpTick(), 1);
		StatsTickID = ClientAPI.Event.RegisterGameTickListener(_ => SendVoiceStats(), 1000);

		CheckCellAndSend(force: true);

		ClientAPI.Logger.Notification("[YHPVC] Connected as voice peer {0}.", msg.SelfPeerId);
	}

	private VoiceHudStatus GetVoiceHudStatus()
	{
		if (Muted) return VoiceHudStatus.Muted;
		if (Capture?.IsTransmittingRecently(300) == true) return VoiceHudStatus.Talking;
		if (Hello != null && Capture?.Running == true) return VoiceHudStatus.Idle;
		return VoiceHudStatus.Muted;
	}

	private bool ShouldTransmitPushToTalk()
	{
		try { return ClientAPI.Input.IsHotKeyPressed(YHPVCConstants.PushToTalkHotkeyCode); }
		catch { return false; }
	}

	private bool IsMuted() { return Muted; }

	private bool OnMuteHotkey(KeyCombination _)
	{
		Muted = !Muted;
		return true;
	}

	private void SendVoiceFrame(ClientVoiceFrame frame)
	{
		if (VoiceChannel == null || !VoiceChannel.Connected) return;

		byte[] payload = VoiceBinaryProtocol.WriteClientFrame(frame);
		VoiceChannel.SendPacket(new VoiceUDPDatagram { Payload = payload });
	}

	private void EnqueueLocalMonitorFrame(ReadOnlySpan<short> pcm) { Mixer?.EnqueueLocalMonitorFrame(pcm); }

	private void OnPeerTable(PeerTableMsg msg) { PeerTable.SetAll(msg.Peers ?? Array.Empty<PeerJoinedMsg>()); }
	private void OnPeerJoined(PeerJoinedMsg msg) { PeerTable.Add(msg); }
	private void OnPeerLeft(PeerLeftMsg msg)
	{
		PeerTable.Remove(msg.PeerID);
		ushort peerId = msg.PeerID;
		ClientAPI.Event.EnqueueMainThreadTask(() => Mixer?.RemovePeer(peerId), "yhpvc-peer-left");
	}

	private void OnCodecControl(CodecControlMsg msg) { Capture?.ApplyCodecControl(msg); }

	private void OnVoiceDatagram(VoiceUDPDatagram datagram)
	{
		if (datagram.Payload == null || datagram.Payload.Length == 0) return;

		List<ServerVoiceFrame> frames = TLSServerBundleFrames ??= new List<ServerVoiceFrame>(32);
		frames.Clear();

		if (!VoiceBinaryProtocol.TryReadServerBundle(datagram.Payload, frames))
		{
			frames.Clear();
			return;
		}

		long receivedMs = Environment.TickCount64;
		for (int i = 0; i < frames.Count; i++) { EnqueueInboundFrame(frames[i], receivedMs); }

		frames.Clear();
	}

	private void EnqueueInboundFrame(ServerVoiceFrame frame, long receivedMs) { InboundFrames.Enqueue(new QueuedServerVoiceFrame(frame, receivedMs)); }

	private void CheckCellAndSend() => CheckCellAndSend(false);

	private void CheckCellAndSend(bool force)
	{
		if (Hello == null || ClientAPI.World.Player?.Entity?.Pos == null) return;

		var pos = ClientAPI.World.Player.Entity.Pos;
		int cellX = VoiceMath.CoordToCell(pos.X, Hello.GridCellSizeBlocks);
		int cellZ = VoiceMath.CoordToCell(pos.Z, Hello.GridCellSizeBlocks);
		int dim = pos.Dimension;

		if (!force && cellX == CurrentCellX && cellZ == CurrentCellZ && dim == CurrentDimension) return;

		CurrentCellX = cellX;
		CurrentCellZ = cellZ;
		CurrentDimension = dim;

		if (ControlChannel.Connected)
		{
			ControlChannel.SendPacket(new ClientCellUpdateMsg
			{
				CellX = cellX,
				CellZ = cellZ,
				Dimension = dim
			});
		}
	}

	private void AudioPumpTick()
	{
		if (Mixer == null || Output == null) return;

		long nowMs = Environment.TickCount64;
		int elapsedMs = (int)Math.Clamp(nowMs - AudioLastTickMS, 0, 250);
		AudioLastTickMS = nowMs;

		AudioAccumulatedMS += elapsedMs;

		int framesDue = AudioAccumulatedMS / AudioFrameMS;
		if (framesDue <= 0) return;

		if (framesDue > MaxMixFramesPerPump)
		{
			framesDue = MaxMixFramesPerPump;
			AudioAccumulatedMS = 0;
			AudioPlaybackClockMS = nowMs - AudioFrameMS;
		}
		else { AudioAccumulatedMS -= framesDue * AudioFrameMS; }

		DrainInboundFrames(framesDue);

		for (int i = 0; i < framesDue; i++)
		{
			AudioPlaybackClockMS += AudioFrameMS;

			PooledPcmFrame pcm = Output.RentFrame(Mixer.OutputSampleCount);
			Mixer.MixNextFrameInto(pcm.Buffer.AsSpan(0, pcm.SampleCount), AudioPlaybackClockMS);
			Output.Queue(pcm);
		}
	}

	private void DrainInboundFrames(int framesDue)
	{
		if (Mixer == null) return;

		int maxDrain = Math.Min(MaxInboundFramesPerPump, Math.Max(MaxInboundFramesPerAudioFrame, framesDue * MaxInboundFramesPerAudioFrame));

		int drained = 0;
		while (drained++ < maxDrain && InboundFrames.TryDequeue(out var queued)) { Mixer.Enqueue(queued.Frame, queued.ReceivedMS); }
	}

	private void SendVoiceStats()
	{
		if (Mixer == null || ControlChannel == null || !ControlChannel.Connected) return;

		VoiceDecodeStats stats = Mixer.TakeStats();
		if (stats.ReceivedPackets == 0 && stats.LostPackets == 0 && stats.LatePackets == 0) return;

		ControlChannel.SendPacket(new ClientVoiceStatsMsg
		{
			ReceivedPackets = stats.ReceivedPackets,
			LostPackets = stats.LostPackets,
			LatePackets = stats.LatePackets
		});
	}

	private void OnLeaveWorld() { Dispose(); }

	private void StopRuntime()
	{
		DisableAttenuationDebug();
		StopEffectPreview();

		if (CellTickID != 0)	{ try { ClientAPI.Event.UnregisterGameTickListener(CellTickID); }		catch { }		CellTickID = 0; }
		if (CaptureTickID != 0)	{ try { ClientAPI.Event.UnregisterGameTickListener(CaptureTickID); }	catch { }		CaptureTickID = 0; }
		if (MixTickID != 0)		{ try { ClientAPI.Event.UnregisterGameTickListener(MixTickID); }		catch { }		MixTickID = 0; }
		if (StatsTickID != 0)	{ try { ClientAPI.Event.UnregisterGameTickListener(StatsTickID); }		catch { }		StatsTickID = 0; }

		try { EnvironmentSampler?.Dispose(); } catch { }
		EnvironmentSampler = null;

		try { Capture?.Dispose(); } catch { }
		Capture = null;
		Waveform.Clear();

		try { Mixer?.Dispose(); } catch { }
		Mixer = null;

		try { Output?.Dispose(); } catch { }
		Output = null;

		while (InboundFrames.TryDequeue(out _)) { }

		CurrentCellX = int.MinValue;
		CurrentCellZ = int.MinValue;
		CurrentDimension = int.MinValue;
	}

	public void Dispose()
	{
		try { ClientAPI.Event.PlayerEntitySpawn -= OnPlayerEntitySpawn; } catch { }
		try { ClientAPI.Event.LeaveWorld -= OnLeaveWorld; } catch { }

		if (ReadyTickID != 0)
		{
			try { ClientAPI.Event.UnregisterGameTickListener(ReadyTickID); } catch { }
			ReadyTickID = 0;
		}

		StopRuntime();
		Hello = null;

		if (EffectDebugDialog != null)
		{
			try { EffectDebugDialog.TryClose(); } catch { }
			try { EffectDebugDialog.Dispose(); } catch { }
			EffectDebugDialog = null;
		}

		if (ConfigDialog != null)
		{
			try { ConfigDialog.TryClose(); } catch { }
			try { ConfigDialog.Dispose(); } catch { }
			ConfigDialog = null;
		}

		if (StatusIcon != null)
		{
			try { ClientAPI.Event.UnregisterRenderer(StatusIcon, EnumRenderStage.Ortho); } catch { }
			try { StatusIcon.Dispose(); } catch { }
			StatusIcon = null;
		}
	}
}
