using System.Collections.Generic;
using Vintagestory.API.Server;
using YHPVC.Common;

namespace YHPVC.Server;

internal sealed class VoiceBundleQueue
{
	private sealed class RecipientQueue
	{
		public RecipientQueue(List<ServerVoiceFrame> frames) { Frames = frames; }
		public List<ServerVoiceFrame> Frames;
	}

	private readonly struct FlushWork
	{
		public FlushWork(VoicePeerState peer, List<ServerVoiceFrame> frames)
		{
			Peer = peer;
			Frames = frames;
		}

		public VoicePeerState Peer { get; }
		public List<ServerVoiceFrame> Frames { get; }
	}

	private readonly object sync = new();
	private readonly Dictionary<ushort, RecipientQueue> pending = new();
	private readonly Stack<List<ServerVoiceFrame>> frameListPool = new();

	public void Enqueue(ushort recipientPeerId, ServerVoiceFrame frame)
	{
		lock (sync)
		{
			if (!pending.TryGetValue(recipientPeerId, out var queue))
			{
				queue = new RecipientQueue(RentFrameListLocked());
				pending[recipientPeerId] = queue;
			}

			queue.Frames.Add(frame);
		}
	}

	public void RemoveRecipient(ushort recipientPeerId)
	{
		lock (sync)
		{
			if (!pending.Remove(recipientPeerId, out var queue)) return;
			ReturnFrameListLocked(queue.Frames);
		}
	}

	public void Flush(VoicePeerRegistry peers, IServerNetworkChannel voiceChannel, int maxPayloadBytes)
	{
		List<FlushWork> work = new();
		List<ushort>? staleRecipients = null;

		lock (sync)
		{
			foreach (var kvp in pending)
			{
				if (!peers.TryGet(kvp.Key, out var peer))
				{
					staleRecipients ??= new List<ushort>();
					staleRecipients.Add(kvp.Key);
					continue;
				}

				if (kvp.Value.Frames.Count == 0) continue;

				List<ServerVoiceFrame> framesToSend = kvp.Value.Frames;
				kvp.Value.Frames = RentFrameListLocked();

				work.Add(new FlushWork(peer, framesToSend));
			}

			if (staleRecipients != null)
			{
				for (int i = 0; i < staleRecipients.Count; i++)
				{
					if (pending.Remove(staleRecipients[i], out var queue)) { ReturnFrameListLocked(queue.Frames); }
				}
			}
		}

		for (int i = 0; i < work.Count; i++)
		{
			try { SendBundlesForRecipient(work[i].Peer, work[i].Frames, voiceChannel, maxPayloadBytes); }
			finally { ReturnFrameList(work[i].Frames); }
		}
	}

	private List<ServerVoiceFrame> RentFrameListLocked()
	{
		if (frameListPool.Count > 0) { return frameListPool.Pop(); }
		return new List<ServerVoiceFrame>(16);
	}

	private void ReturnFrameList(List<ServerVoiceFrame> frames)
	{
		lock (sync) { ReturnFrameListLocked(frames); }
	}

	private void ReturnFrameListLocked(List<ServerVoiceFrame> frames)
	{
		frames.Clear();
		frameListPool.Push(frames);
	}

	private static void SendBundlesForRecipient(VoicePeerState recipient, IReadOnlyList<ServerVoiceFrame> frames, IServerNetworkChannel voiceChannel, int maxPayloadBytes)
	{
		int start = 0;

		while (start < frames.Count)
		{
			int count = 0;
			int size = VoiceBinaryProtocol.BundleHeaderSize;

			while (start + count < frames.Count && count < byte.MaxValue)
			{
				int nextSize = size + VoiceBinaryProtocol.FramePackedSize(frames[start + count]);
				if (count > 0 && nextSize > maxPayloadBytes) break;

				size = nextSize;
				count++;

				if (size >= maxPayloadBytes) break;
			}

			if (count == 0) // Skip oversized frames rather than letting them block the recipient queue
			{
				start++;
				continue;
			}

			byte[] payload = VoiceBinaryProtocol.WriteServerBundle(frames, start, count);
			voiceChannel.SendPacket(new VoiceUDPDatagram { Payload = payload }, recipient.Player);
			start += count;
		}
	}
}
