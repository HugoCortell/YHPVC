using System.Collections.Generic;
using YHPVC.Common;

namespace YHPVC.Client;

internal sealed class VoicePeerTable
{
	private readonly object Sync = new();
	private readonly Dictionary<ushort, PeerJoinedMsg> ByPeerID = new();

	public ushort LocalPeerID { get; set; }

	public void SetAll(PeerJoinedMsg[] peers)
	{
		lock (Sync)
		{
			ByPeerID.Clear();
			for (int i = 0; i < peers.Length; i++) ByPeerID[peers[i].PeerID] = peers[i];
		}
	}

	public void Add(PeerJoinedMsg peer) { lock (Sync) ByPeerID[peer.PeerID] = peer; }
	public void Remove(ushort peerId) { lock (Sync) ByPeerID.Remove(peerId); }
	public bool TryGet(ushort peerId, out PeerJoinedMsg peer) { lock (Sync) return ByPeerID.TryGetValue(peerId, out peer!); }
}
