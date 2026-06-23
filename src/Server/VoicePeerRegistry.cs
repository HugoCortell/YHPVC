using System;
using System.Collections.Generic;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using YHPVC.Common;

namespace YHPVC.Server;

internal sealed class VoicePeerRegistry
{
	private readonly PlayerHashGrid2D Grid;
	private readonly int DefaultBitrateKBPS;

	private ushort NextPeerId = 1;

	private readonly Queue<ushort> FreePeerIDS = new();
	private readonly Dictionary<string, VoicePeerState> ByUID = new();
	private readonly Dictionary<ushort, VoicePeerState> ByPeerID = new();

	public VoicePeerRegistry(PlayerHashGrid2D grid, int defaultBitrateKbps)
	{
		this.Grid = grid;
		this.DefaultBitrateKBPS = defaultBitrateKbps;
	}

	public IReadOnlyDictionary<ushort, VoicePeerState> ByPeerId => ByPeerID;

	public bool TryGet(IServerPlayer player, out VoicePeerState peer) => ByUID.TryGetValue(player.PlayerUID, out peer!);
	public bool TryGet(ushort peerId, out VoicePeerState peer) => ByPeerID.TryGetValue(peerId, out peer!);

	public VoicePeerState GetOrAdd(IServerPlayer player, long nowMs, out bool added)
	{
		if (ByUID.TryGetValue(player.PlayerUID, out var existing))
		{
			existing.Player = player;
			existing.PlayerName = player.PlayerName;
			added = false;
			return existing;
		}

		EntityPos pos = player.Entity.Pos;

		var peer = new VoicePeerState
		{
			PeerID = AllocatePeerId(),
			Player = player,
			PlayerUID = player.PlayerUID,
			PlayerName = player.PlayerName,
			Dimension = pos.Dimension,
			CellX = Grid.CoordToCell(pos.X),
			CellZ = Grid.CoordToCell(pos.Z),
			LastCellUpdateMS = nowMs,
			LastRepairMS = nowMs,
			TargetBitrateKBPS = DefaultBitrateKBPS,
			LastQualityRaiseMS = nowMs
		};

		ByUID[peer.PlayerUID] = peer;
		ByPeerID[peer.PeerID] = peer;
		Grid.Add(peer.Dimension, peer.CellX, peer.CellZ, peer.PeerID);

		added = true;
		return peer;
	}

	public VoicePeerState? Remove(IServerPlayer player)
	{
		if (!ByUID.Remove(player.PlayerUID, out var peer)) return null;

		ByPeerID.Remove(peer.PeerID);
		Grid.Remove(peer.Dimension, peer.CellX, peer.CellZ, peer.PeerID);
		FreePeerIDS.Enqueue(peer.PeerID);
		return peer;
	}

	public void RepairCell(VoicePeerState peer, long nowMs)
	{
		if (peer.Player.Entity?.Pos == null) return;

		EntityPos pos = peer.Player.Entity.Pos;
		int cellX = Grid.CoordToCell(pos.X);
		int cellZ = Grid.CoordToCell(pos.Z);
		Grid.Move(peer, pos.Dimension, cellX, cellZ);
		peer.LastRepairMS = nowMs;
	}

	public void UpdateCellFromClient(VoicePeerState peer, int dimension, int cellX, int cellZ, long nowMs)
	{
		Grid.Move(peer, dimension, cellX, cellZ);
		peer.LastCellUpdateMS = nowMs;
	}

	private ushort AllocatePeerId()
	{
		while (FreePeerIDS.Count > 0)
		{
			ushort id = FreePeerIDS.Dequeue();
			if (id != 0 && !ByPeerID.ContainsKey(id)) return id;
		}

		if (NextPeerId == 0) NextPeerId = 1;

		ushort allocated = NextPeerId++;
		while (allocated == 0 || ByPeerID.ContainsKey(allocated))
		{
			allocated = NextPeerId++;
			if (allocated == 0) NextPeerId = 1;
		}

		return allocated;
	}
}

internal sealed class VoicePeerState
{
	public ushort PeerID;
	public IServerPlayer Player = null!;
	public string PlayerUID = "";
	public string PlayerName = "";

	public int Dimension;
	public int CellX;
	public int CellZ;

	public long LastCellUpdateMS;
	public long LastRepairMS;

	public int TargetBitrateKBPS;
	public bool TargetFEC;
	public int TargetPacketLossPercent;
	public int LastFanout;

	public int LastReportedLossPercent;
	public long LastStatsMS;
	public long LastCodecControlMS;
	public long LastQualityRaiseMS;
}