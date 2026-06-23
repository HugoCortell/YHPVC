using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace YHPVC.Common;

public readonly struct ClientVoiceFrame
{
	public ClientVoiceFrame(ushort sequence, byte frameDurationCode, byte flags, byte audioLevel, byte[] opus)
	{
		Sequence = sequence;
		FrameDurationCode = frameDurationCode;
		Flags = flags;
		AudioLevel = audioLevel;
		Opus = opus;
	}

	public ushort Sequence { get; }
	public byte FrameDurationCode { get; }
	public byte Flags { get; }
	public byte AudioLevel { get; }
	public byte[] Opus { get; }
}

public readonly struct ServerVoiceFrame
{
	public ServerVoiceFrame(ushort speakerPeerId, ushort sequence, byte frameDurationCode, byte flags, byte audioLevel, byte[] opus)
	{
		SpeakerPeerID = speakerPeerId;
		Sequence = sequence;
		FrameDurationCode = frameDurationCode;
		Flags = flags;
		AudioLevel = audioLevel;
		Opus = opus;
	}

	public ushort SpeakerPeerID { get; }
	public ushort Sequence { get; }
	public byte FrameDurationCode { get; }
	public byte Flags { get; }
	public byte AudioLevel { get; }
	public byte[] Opus { get; }
}

public static class VoiceBinaryProtocol
{
	private const int U16 = sizeof(ushort);
	private const int U8 = sizeof(byte);

	private const int SequenceSize = U16;
	private const int PeerIdSize = U16;
	private const int FrameDurationSize = U8;
	private const int FlagsSize = U8;
	private const int AudioLevelSize = U8;
	private const int OpusLengthSize = U16;
	private const int FrameCountSize = U8;

	private const int ClientFrameHeaderSize = SequenceSize + FrameDurationSize + FlagsSize + AudioLevelSize + OpusLengthSize;
	private const int ServerFrameHeaderSize = PeerIdSize + SequenceSize + FrameDurationSize + FlagsSize + AudioLevelSize + OpusLengthSize;

	private const int ServerBundleHeaderSize = FrameCountSize;

	public static int BundleHeaderSize => ServerBundleHeaderSize;

	public static int ClientPayloadSize(int opusLength) => ClientFrameHeaderSize + opusLength;

	public static int FramePackedSize(ServerVoiceFrame frame) => ServerFrameHeaderSize + frame.Opus.Length;

	public static byte[] WriteClientFrame(ClientVoiceFrame frame)
	{
		if (frame.Opus.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(frame), "Opus frame is too large!");

		byte[] payload = new byte[ClientFrameHeaderSize + frame.Opus.Length];
		Span<byte> span = payload;

		int o = 0;
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, U16), frame.Sequence); o += U16;
		span[o++] = frame.FrameDurationCode;
		span[o++] = frame.Flags;
		span[o++] = frame.AudioLevel;
		BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, U16), (ushort)frame.Opus.Length); o += U16;
		frame.Opus.CopyTo(payload, o);

		return payload;
	}

	public static bool TryReadClientFrame(ReadOnlySpan<byte> payload, out ClientVoiceFrame frame)
	{
		frame = default;
		if (payload.Length < ClientFrameHeaderSize) return false;

		int o = 0;
		ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o, U16)); o += U16;
		byte duration = payload[o++];
		if (duration != YHPVCConstants.FrameDuration20Ms && duration != YHPVCConstants.FrameDuration40Ms) return false;

		byte flags = payload[o++];
		byte level = payload[o++];
		int opusLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o, U16)); o += U16;
		if (opusLength <= 0 || payload.Length != o + opusLength) return false;

		byte[] opus = payload.Slice(o, opusLength).ToArray();
		frame = new ClientVoiceFrame(sequence, duration, flags, level, opus);
		return true;
	}

	public static byte[] WriteServerBundle(IReadOnlyList<ServerVoiceFrame> frames, int startIndex, int count)
	{
		if (count < 0 || count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(count));

		int size = ServerBundleHeaderSize;
		for (int i = 0; i < count; i++)
		{
			ServerVoiceFrame frame = frames[startIndex + i];
			if (frame.Opus.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(frames), "Opus frame is too large!");
			size += FramePackedSize(frame);
		}

		byte[] payload = new byte[size];
		Span<byte> span = payload;

		int o = 0;
		span[o++] = (byte)count;
		for (int i = 0; i < count; i++)
		{
			ServerVoiceFrame frame = frames[startIndex + i];

			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, U16), frame.SpeakerPeerID); o += U16;
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, U16), frame.Sequence); o += U16;
			span[o++] = frame.FrameDurationCode;
			span[o++] = frame.Flags;
			span[o++] = frame.AudioLevel;
			BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(o, U16), (ushort)frame.Opus.Length); o += U16;

			frame.Opus.CopyTo(payload, o);
			o += frame.Opus.Length;
		}

		return payload;
	}

	public static bool TryReadServerBundle(ReadOnlySpan<byte> payload, List<ServerVoiceFrame> into)
	{
		if (payload.Length < ServerBundleHeaderSize) return false;

		int o = 0;
		int frameCount = payload[o++];

		for (int i = 0; i < frameCount; i++)
		{
			if (payload.Length < o + ServerFrameHeaderSize) return false;

			ushort speakerId = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o, U16)); o += U16;
			ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o, U16)); o += U16;
			byte duration = payload[o++];
			if (duration != YHPVCConstants.FrameDuration20Ms && duration != YHPVCConstants.FrameDuration40Ms) return false;

			byte flags = payload[o++];
			byte level = payload[o++];
			int opusLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o, U16)); o += U16;

			if (opusLength <= 0 || payload.Length < o + opusLength) return false;

			byte[] opus = payload.Slice(o, opusLength).ToArray();
			o += opusLength;

			into.Add(new ServerVoiceFrame(speakerId, sequence, duration, flags, level, opus));
		}

		return o == payload.Length;
	}
}
