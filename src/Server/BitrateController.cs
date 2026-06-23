using YHPVC.Common;

namespace YHPVC.Server;

internal sealed class BitrateController
{
	private readonly YHPVCServerConfig ServerConfig;

	public BitrateController(YHPVCServerConfig cfg) { this.ServerConfig = cfg; }

	public int TargetForFanout(int fanout)
	{
		if (!ServerConfig.DynamicBitrate) return ServerConfig.DefaultBitrateKBPS;

		int target =
			fanout <= 8 ? ServerConfig.MaxBitrateKBPS :
			fanout <= 16 ? System.Math.Min(ServerConfig.MaxBitrateKBPS, 20) :
			fanout <= 32 ? System.Math.Min(ServerConfig.MaxBitrateKBPS, 16) :
			fanout <= 64 ? System.Math.Min(ServerConfig.MaxBitrateKBPS, 12) :
			System.Math.Min(ServerConfig.MaxBitrateKBPS, 8);

		return System.Math.Clamp(target, ServerConfig.MinBitrateKBPS, ServerConfig.MaxBitrateKBPS);
	}

	public bool ShouldEnableFec(int maxReportedLossPercent, int currentFanout)
	{
		if (!ServerConfig.AdaptiveFEC) return false;
		if (ServerConfig.FECDisableAboveFanout > 0 && currentFanout > ServerConfig.FECDisableAboveFanout) return false;
		
		return maxReportedLossPercent >= ServerConfig.FECLossEnablePercent;
	}

	public bool ShouldKeepFec(bool currentlyEnabled, int maxReportedLossPercent, int currentFanout)
	{
		if (!currentlyEnabled) return ShouldEnableFec(maxReportedLossPercent, currentFanout);
		if (!ServerConfig.AdaptiveFEC) return false;
		if (ServerConfig.FECDisableAboveFanout > 0 && currentFanout > ServerConfig.FECDisableAboveFanout) return false;
		
		return maxReportedLossPercent > ServerConfig.FECLossDisablePercent;
	}
}
