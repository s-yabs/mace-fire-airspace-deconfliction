using BSI.MACE;
using BSI.MACE.CallForFire;

namespace MaceFireAirspace;

internal sealed class CallForFireMissionSnapshot
{
    public int RequestId { get; set; }
    public int DisplayIndex { get; set; }
    public string TargetNumber { get; set; } = "";
    public string Round { get; set; } = "";
    public int NumberOfRounds { get; set; }
    public string BatteryName { get; set; } = "";
    public ulong BatteryId { get; set; }
    public ulong BatteryAggregateId { get; set; }
    public string TargetLocationText { get; set; } = "";
    public IGeoPoint? TargetPoint { get; set; }
    public double GunTargetLine_deg { get; set; }
    public double MaxOrdinateMSL_m { get; set; }
    public double TimeOfFlight_s { get; set; }
    public string Status { get; set; } = "";
    public int CffFormNumber => DisplayIndex < 0 ? 0 : (DisplayIndex / 8) + 1;
    public int MissionNumber => DisplayIndex < 0 ? 0 : (DisplayIndex % 8) + 1;
    public string CffFormName => CffFormNumber > 0 && CffFormNumber <= 4 ? $"CFF Form {CffFormNumber}" : $"CFF Form ?";
    public string MissionName => MissionNumber > 0 ? $"Mission {MissionNumber}" : "Mission ?";

    public static CallForFireMissionSnapshot FromMission(
        ICallForFire.CallForFireEventArgs.CallForFireMission mission,
        IMap? map,
        int displayIndex)
    {
        IGeoPoint? targetPoint = null;
        if (!string.IsNullOrWhiteSpace(mission.TargetLocation) && map != null)
        {
            try
            {
                targetPoint = map.GetIGeoPointFromText(mission.TargetLocation);
            }
            catch
            {
                targetPoint = null;
            }
        }

        var aggregate = mission.Battery?.GetAggregate();
        var batteryName = aggregate?.Name;
        if (string.IsNullOrWhiteSpace(batteryName))
        {
            batteryName = string.IsNullOrWhiteSpace(mission.Battery?.Label)
                ? mission.Battery?.Name
                : mission.Battery?.Label;
        }

        return new CallForFireMissionSnapshot
        {
            RequestId = mission.RequestID,
            DisplayIndex = displayIndex,
            TargetNumber = mission.TargetNumber ?? "",
            Round = mission.Round ?? "",
            NumberOfRounds = mission.NumberOfRounds,
            BatteryName = batteryName ?? "",
            BatteryId = mission.Battery?.ID ?? 0,
            BatteryAggregateId = aggregate?.ID ?? 0,
            TargetLocationText = mission.TargetLocation ?? "",
            TargetPoint = targetPoint,
            GunTargetLine_deg = mission.GunTargetLine_deg,
            MaxOrdinateMSL_m = mission.MaxOrdinateMSL_m,
            TimeOfFlight_s = mission.TimeOfFlight_s,
            Status = mission.Status.ToString()
        };
    }
}
