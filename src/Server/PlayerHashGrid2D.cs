using System;
using System.Collections.Generic;
using YHPVC.Common;

namespace YHPVC.Server;

internal sealed class PlayerHashGrid2D // Sparse (todo, consider a better spatial hashing algo?)
{
	public readonly int CellSize;
	public readonly int CellShift;

	private readonly Dictionary<int, Dictionary<long, List<ushort>>> cellsByDim = new();

	public PlayerHashGrid2D(int cellSizeBlocks)
	{
		if (cellSizeBlocks <= 0) throw new ArgumentOutOfRangeException(nameof(cellSizeBlocks));
		if ((cellSizeBlocks & (cellSizeBlocks - 1)) != 0) throw new ArgumentException("Cell size not pair of two.", nameof(cellSizeBlocks));

		CellSize = cellSizeBlocks;

		int shift = 0;
		int v = cellSizeBlocks;
		while ((v >>= 1) != 0) shift++;
		CellShift = shift;
	}

	public int BlockToCell(int blockCoord) => blockCoord >> CellShift;
	public int CoordToCell(double coord) => BlockToCell(VoiceMath.FloorBlockCoord(coord));
	
	public static long CellKey(int cx, int cz) => ((long)cx << 32) | (uint)cz;

	public void Clear() => cellsByDim.Clear();

	public void Add(int dim, int cellX, int cellZ, ushort peerId)
	{
		if (!cellsByDim.TryGetValue(dim, out var dimMap))
		{
			dimMap = new Dictionary<long, List<ushort>>(256);
			cellsByDim[dim] = dimMap;
		}

		long key = CellKey(cellX, cellZ);
		if (!dimMap.TryGetValue(key, out var bucket))
		{
			bucket = new List<ushort>(4);
			dimMap[key] = bucket;
		}

		bucket.Add(peerId);
	}

	public void Remove(int dim, int cellX, int cellZ, ushort peerId)
	{
		if (!cellsByDim.TryGetValue(dim, out var dimMap)) return;

		long key = CellKey(cellX, cellZ);
		if (!dimMap.TryGetValue(key, out var bucket)) return;

		for (int i = 0; i < bucket.Count; i++)
		{
			if (bucket[i] != peerId) continue;

			int last = bucket.Count - 1;
			bucket[i] = bucket[last];
			bucket.RemoveAt(last);
			break;
		}

		if (bucket.Count == 0) dimMap.Remove(key);
		if (dimMap.Count == 0) cellsByDim.Remove(dim);
	}

	public void Move(VoicePeerState peer, int newDim, int newCellX, int newCellZ)
	{
		if (peer.Dimension == newDim && peer.CellX == newCellX && peer.CellZ == newCellZ) return;

		Remove(peer.Dimension, peer.CellX, peer.CellZ, peer.PeerID);

		peer.Dimension = newDim;
		peer.CellX = newCellX;
		peer.CellZ = newCellZ;

		Add(peer.Dimension, peer.CellX, peer.CellZ, peer.PeerID);
	}

	public int CollectNear(int dim, int cellX, int cellZ, int radiusBlocks, List<ushort> into, bool clear = true)
	{
		if (clear) into.Clear();
		if (!cellsByDim.TryGetValue(dim, out var dimMap)) return 0;

		int rCells = (radiusBlocks + (CellSize - 1)) >> CellShift;

		for (int z = cellZ - rCells; z <= cellZ + rCells; z++)
		{
			for (int x = cellX - rCells; x <= cellX + rCells; x++)
			{
				if (!dimMap.TryGetValue(CellKey(x, z), out var bucket) || bucket.Count == 0) continue;
				into.AddRange(bucket);
			}
		}

		return into.Count;
	}
}
