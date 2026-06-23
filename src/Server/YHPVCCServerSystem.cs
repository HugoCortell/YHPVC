using System;
using System.Collections.Generic;
using Vintagestory.API.Server;
using YHPVC.Common;

namespace YHPVC.Server;

public sealed class YHPVCServerSystem : IDisposable
{
	private readonly ICoreServerAPI ServerAPI;
	private readonly YHPVCServerConfig ServerConfig;
	private readonly PlayerHashGrid2D HashGrid;
	private readonly VoicePeerRegistry Peers;
	private readonly VoiceBundleQueue BundleQueue = new();
	private readonly BitrateController BitrateController;

	private IServerNetworkChannel controlChannel = null!;
	private IServerNetworkChannel VoiceChannel = null!;

	private long RepairTickID;
	private long BundleTickID;

	[ThreadStatic]
	private static List<ushort>? TLSCandidatePeerIds;

	public YHPVCServerSystem(ICoreServerAPI api, YHPVCServerConfig cfg)
	{
		this.ServerAPI = api;
		this.ServerConfig = cfg;

		HashGrid = new PlayerHashGrid2D(cfg.GridCellSizeBlocks);
		Peers = new VoicePeerRegistry(HashGrid, cfg.DefaultBitrateKBPS);
		BitrateController = new BitrateController(cfg);
	}

	public void Start()
	{
		controlChannel = ServerAPI.Network
			.GetChannel(YHPVCConstants.ControlChannel)
			.SetMessageHandler<ClientReadyMsg>(OnClientReady)
			.SetMessageHandler<ClientCellUpdateMsg>(OnClientCellUpdate)
			.SetMessageHandler<ClientVoiceStatsMsg>(OnClientVoiceStats);

		VoiceChannel = ServerAPI.Network
			.GetUdpChannel(YHPVCConstants.VoiceChannel)
			.SetMessageHandler<VoiceUDPDatagram>(OnVoiceDatagram);

		ServerAPI.Event.PlayerDisconnect += OnPlayerDisconnect;

		RepairTickID = ServerAPI.Event.RegisterGameTickListener(_ => RepairGrid(), ServerConfig.ServerGridRepairIntervalMS);
		BundleTickID = ServerAPI.Event.RegisterGameTickListener(_ => BundleQueue.Flush(Peers, VoiceChannel, ServerConfig.MaxVoicePayloadBytes), ServerConfig.BundleFlushIntervalMs);

		ServerAPI.Logger.Notification("[YHPVC] Server system started.");
	}

	private void OnClientReady(IServerPlayer player, ClientReadyMsg msg)
	{
		if (player.Entity?.Pos == null) return;

		long now = Environment.TickCount64;
		VoicePeerState peer = Peers.GetOrAdd(player, now, out bool added);

		controlChannel.SendPacket(new ServerHelloMsg
		{
			SelfPeerId = peer.PeerID,
			GridCellSizeBlocks = ServerConfig.GridCellSizeBlocks,
			AudibleRangeBlocks = ServerConfig.AudibleRangeBlocks,
			RoutePaddingBlocks = ServerConfig.RoutePaddingBlocks,
			FullVolumeDistanceBlocks = ServerConfig.FullVolumeDistanceBlocks,
			SampleRate = ServerConfig.SampleRate,
			FrameDurationMS = ServerConfig.FrameDurationMS,
			MinBitrateKbps = ServerConfig.MinBitrateKBPS,
			MaxBitrateKbps = ServerConfig.MaxBitrateKBPS,
			DefaultBitrateKBPS = ServerConfig.DefaultBitrateKBPS,
			OpusComplexity = ServerConfig.OpusComplexity,
			DTX = ServerConfig.DTX,
			FEC = peer.TargetFEC,
			MaxVoicePayloadBytes = ServerConfig.MaxVoicePayloadBytes,
			ImmersiveEffects = ServerConfig.ImmersiveEffects
		}, player);

		SendPeerTable(player);
		if (added) BroadcastPeerJoined(peer, except: player);

		if (ServerConfig.DebugLogging) ServerAPI.Logger.Notification("[YHPVC] Registered peer {0} for {1}.", peer.PeerID, peer.PlayerName);
	}

	private void SendPeerTable(IServerPlayer recipient)
	{
		var list = new List<PeerJoinedMsg>(Peers.ByPeerId.Count);
		foreach (VoicePeerState peer in Peers.ByPeerId.Values)
		{
			list.Add(new PeerJoinedMsg
			{
				PeerID = peer.PeerID,
				PlayerUID = peer.PlayerUID,
				PlayerName = peer.PlayerName
			});
		}

		controlChannel.SendPacket(new PeerTableMsg { Peers = list.ToArray() }, recipient);
	}

	private void BroadcastPeerJoined(VoicePeerState peer, IServerPlayer except)
	{
		var msg = new PeerJoinedMsg
		{
			PeerID = peer.PeerID,
			PlayerUID = peer.PlayerUID,
			PlayerName = peer.PlayerName
		};

		foreach (var raw in ServerAPI.World.AllOnlinePlayers)
		{
			if (raw is not IServerPlayer recipient) continue;
			if (recipient.PlayerUID == except.PlayerUID) continue;
			if (!Peers.TryGet(recipient, out _)) continue;
			controlChannel.SendPacket(msg, recipient);
		}
	}

	private void OnClientCellUpdate(IServerPlayer player, ClientCellUpdateMsg msg)
	{
		if (!Peers.TryGet(player, out var peer)) return;

		long now = Environment.TickCount64;
		Peers.UpdateCellFromClient(peer, msg.Dimension, msg.CellX, msg.CellZ, now);
	}


	private void OnClientVoiceStats(IServerPlayer player, ClientVoiceStatsMsg msg)
	{
		if (!Peers.TryGet(player, out var peer)) return;

		int total = msg.ReceivedPackets + msg.LostPackets;
		int lossPercent = total <= 0 ? 0 : (int)Math.Round(msg.LostPackets * 100.0 / total);

		peer.LastReportedLossPercent = Math.Clamp(lossPercent, 0, 100);
		peer.LastStatsMS = Environment.TickCount64;

		RecalculateFecTargets();
	}

	private void OnVoiceDatagram(IServerPlayer sender, VoiceUDPDatagram datagram)
	{
		if (datagram.Payload == null || datagram.Payload.Length == 0) return;
		if (datagram.Payload.Length > ServerConfig.MaxVoicePayloadBytes) return;
		if (!Peers.TryGet(sender, out var speaker)) return;
		if (!VoiceBinaryProtocol.TryReadClientFrame(datagram.Payload, out var frame)) return;

		var serverFrame = new ServerVoiceFrame(speaker.PeerID, frame.Sequence, frame.FrameDurationCode, frame.Flags, frame.AudioLevel, frame.Opus);
		if (VoiceBinaryProtocol.BundleHeaderSize + VoiceBinaryProtocol.FramePackedSize(serverFrame) > ServerConfig.MaxVoicePayloadBytes) return;

		List<ushort> candidates = TLSCandidatePeerIds ??= new List<ushort>(256);
		candidates.Clear();
		HashGrid.CollectNear(speaker.Dimension, speaker.CellX, speaker.CellZ, ServerConfig.ServerRouteRadiusBlocks, candidates, clear: false);

		int fanout = 0;

		for (int i = 0; i < candidates.Count; i++)
		{
			ushort recipientId = candidates[i];
			if (recipientId == speaker.PeerID) continue;
			if (!Peers.TryGet(recipientId, out _)) continue;

			BundleQueue.Enqueue(recipientId, serverFrame);
			fanout++;
		}

		candidates.Clear();

		speaker.LastFanout = fanout;
		int targetKbps = BitrateController.TargetForFanout(fanout);
		int maxLoss = MaxRecentReportedLossPercent();
		bool targetFec = BitrateController.ShouldKeepFec(speaker.TargetFEC, maxLoss, fanout);

		MaybeSendCodecControl(speaker, targetKbps, targetFec, maxLoss);
	}

	private void MaybeSendCodecControl(VoicePeerState peer, int targetKbps, bool targetFec, int packetLossPercent)
	{
		long now = Environment.TickCount64;

		bool bitrateChanged = targetKbps != peer.TargetBitrateKBPS;
		bool fecChanged = targetFec != peer.TargetFEC;
		if (!bitrateChanged && !fecChanged && packetLossPercent == peer.TargetPacketLossPercent) return;

		bool loweringBitrate = targetKbps < peer.TargetBitrateKBPS;
		bool raisingBitrate = targetKbps > peer.TargetBitrateKBPS;
		bool disablingFec = !targetFec && peer.TargetFEC;

		if (peer.LastCodecControlMS != 0 && now - peer.LastCodecControlMS < ServerConfig.CodecControlMinIntervalMS && !loweringBitrate && !disablingFec) return;
		if (raisingBitrate && now - peer.LastQualityRaiseMS < ServerConfig.CodecQualityRaiseIntervalMS) return;

		peer.TargetBitrateKBPS = targetKbps;
		peer.TargetFEC = targetFec;
		peer.TargetPacketLossPercent = Math.Clamp(packetLossPercent, 0, 50);
		peer.LastCodecControlMS = now;
		if (raisingBitrate) peer.LastQualityRaiseMS = now;

		controlChannel.SendPacket(new CodecControlMsg
		{
			TargetBitrateKbps = peer.TargetBitrateKBPS,
			FEC = peer.TargetFEC,
			PacketLossPercent = peer.TargetPacketLossPercent
		}, peer.Player);
	}

	private void RecalculateFecTargets()
	{
		int maxLoss = MaxRecentReportedLossPercent();
		foreach (VoicePeerState peer in Peers.ByPeerId.Values)
		{
			bool targetFec = BitrateController.ShouldKeepFec(peer.TargetFEC, maxLoss, peer.LastFanout);
			MaybeSendCodecControl(peer, peer.TargetBitrateKBPS, targetFec, maxLoss);
		}
	}

	private int MaxRecentReportedLossPercent()
	{
		long now = Environment.TickCount64;
		int max = 0;

		foreach (VoicePeerState peer in Peers.ByPeerId.Values)
		{
			if (peer.LastStatsMS == 0 || now - peer.LastStatsMS > 10000) continue;
			if (peer.LastReportedLossPercent > max) max = peer.LastReportedLossPercent;
		}

		return max;
	}


	private void RepairGrid()
	{
		long now = Environment.TickCount64;

		foreach (VoicePeerState peer in Peers.ByPeerId.Values)
		{
			if (peer.Player.Entity?.Pos == null) continue;
			Peers.RepairCell(peer, now);
		}
	}

	private void OnPlayerDisconnect(IServerPlayer player)
	{
		VoicePeerState? removed = Peers.Remove(player);
		if (removed == null) return;

		BundleQueue.RemoveRecipient(removed.PeerID);

		var msg = new PeerLeftMsg { PeerID = removed.PeerID };
		foreach (var raw in ServerAPI.World.AllOnlinePlayers)
		{
			if (raw is not IServerPlayer recipient) continue;
			if (!Peers.TryGet(recipient, out _)) continue;
			controlChannel.SendPacket(msg, recipient);
		}
	}

	public void Dispose()
	{
		try { ServerAPI.Event.PlayerDisconnect -= OnPlayerDisconnect; } catch { }

		if (RepairTickID != 0)
		{
			try { ServerAPI.Event.UnregisterGameTickListener(RepairTickID); } catch { }
			RepairTickID = 0;
		}

		if (BundleTickID != 0)
		{
			try { ServerAPI.Event.UnregisterGameTickListener(BundleTickID); } catch { }
			BundleTickID = 0;
		}
	}
}
