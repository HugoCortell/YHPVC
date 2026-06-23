using ProtoBuf;
using System;

namespace YHPVC.Common;

[ProtoContract]
public sealed class ClientReadyMsg { }

[ProtoContract]
public sealed class ServerHelloMsg
{
	[ProtoMember(1)] public ushort SelfPeerId { get; set; }

	[ProtoMember(2)] public int GridCellSizeBlocks { get; set; }
	[ProtoMember(3)] public int AudibleRangeBlocks { get; set; }
	[ProtoMember(4)] public int RoutePaddingBlocks { get; set; }
	[ProtoMember(15)] public float FullVolumeDistanceBlocks { get; set; } = 2f;

	[ProtoMember(5)] public int SampleRate { get; set; }
	[ProtoMember(6)] public int FrameDurationMS { get; set; }
	[ProtoMember(7)] public int MinBitrateKbps { get; set; }
	[ProtoMember(8)] public int MaxBitrateKbps { get; set; }
	[ProtoMember(9)] public int DefaultBitrateKBPS { get; set; }
	[ProtoMember(10)] public int OpusComplexity { get; set; }
	[ProtoMember(11)] public bool DTX { get; set; }
	[ProtoMember(12)] public bool FEC { get; set; }
	[ProtoMember(13)] public int MaxVoicePayloadBytes { get; set; }
	[ProtoMember(14)] public bool ImmersiveEffects { get; set; } = true;
}

[ProtoContract]
public sealed class PeerJoinedMsg
{
	[ProtoMember(1)] public ushort PeerID { get; set; }
	[ProtoMember(2)] public string PlayerUID { get; set; } = "";
	[ProtoMember(3)] public string PlayerName { get; set; } = "";
}

[ProtoContract]
public sealed class PeerLeftMsg
{
	[ProtoMember(1)] public ushort PeerID { get; set; }
}

[ProtoContract]
public sealed class PeerTableMsg
{
	[ProtoMember(1)] public PeerJoinedMsg[] Peers { get; set; } = System.Array.Empty<PeerJoinedMsg>();
}

[ProtoContract]
public sealed class ClientCellUpdateMsg
{
	[ProtoMember(1)] public int CellX { get; set; }
	[ProtoMember(2)] public int CellZ { get; set; }
	[ProtoMember(3)] public int Dimension { get; set; }
}

[ProtoContract]
public sealed class CodecControlMsg
{
	[ProtoMember(1)] public int TargetBitrateKbps { get; set; }
	[ProtoMember(2)] public bool FEC { get; set; }
	[ProtoMember(3)] public int PacketLossPercent { get; set; }
}

[ProtoContract]
public sealed class ClientVoiceStatsMsg
{
	[ProtoMember(1)] public int ReceivedPackets { get; set; }
	[ProtoMember(2)] public int LostPackets { get; set; }
	[ProtoMember(3)] public int LatePackets { get; set; }
}

[ProtoContract]
public sealed class VoiceUDPDatagram
{
	[ProtoMember(1)] public byte[] Payload { get; set; } = Array.Empty<byte>();
}

