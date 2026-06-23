using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using YHPVC.Client;
using YHPVC.Common;
using YHPVC.Server;

namespace YHPVC;

public sealed class YHPVCCore : ModSystem
{
	private YHPVCClientSystem? ClientSystem;
	private YHPVCServerSystem? ServerSystem;

	public override bool ShouldLoad(EnumAppSide forSide) => true;

	public override void Start(ICoreAPI api)
	{
		api.Network
			.RegisterChannel(YHPVCConstants.ControlChannel)
			.RegisterMessageType<ClientReadyMsg>()
			.RegisterMessageType<ServerHelloMsg>()
			.RegisterMessageType<PeerJoinedMsg>()
			.RegisterMessageType<PeerLeftMsg>()
			.RegisterMessageType<PeerTableMsg>()
			.RegisterMessageType<ClientCellUpdateMsg>()
			.RegisterMessageType<CodecControlMsg>()
			.RegisterMessageType<ClientVoiceStatsMsg>();

		api.Network
			.RegisterUdpChannel(YHPVCConstants.VoiceChannel)
			.RegisterMessageType<VoiceUDPDatagram>();
	}

	public override void StartServerSide(ICoreServerAPI api)
	{
		var cfg = api.LoadModConfig<YHPVCServerConfig>(YHPVCConstants.ServerConfigFile) ?? new YHPVCServerConfig();
		cfg.Sanitize();
		api.StoreModConfig(cfg, YHPVCConstants.ServerConfigFile);

		ServerSystem = new YHPVCServerSystem(api, cfg);
		ServerSystem.Start();
	}

	public override void StartClientSide(ICoreClientAPI api)
	{
		var cfg = api.LoadModConfig<YHPVCClientConfig>(YHPVCConstants.ClientConfigFile) ?? new YHPVCClientConfig();
		cfg.Sanitize();
		api.StoreModConfig(cfg, YHPVCConstants.ClientConfigFile);

		OpusNativeLoader.EnsureLoaded(api);

		ClientSystem = new YHPVCClientSystem(api, cfg);
		ClientSystem.Start();
	}

	public override void Dispose()
	{
		ClientSystem?.Dispose();
		ClientSystem = null;

		ServerSystem?.Dispose();
		ServerSystem = null;
	}
}
