using System;
using System.Globalization;
using System.Linq;
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
    public string TimeText { get; set; } = "";
    public string MethodOfControlText { get; set; } = "";
    public string SeadMissionTypeText { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime? ScheduledExecutionTime { get; set; }
    public int CffFormNumber => DisplayIndex < 0 ? 0 : (DisplayIndex / 8) + 1;
    public int MissionNumber => DisplayIndex < 0 ? 0 : (DisplayIndex % 8) + 1;
    public string CffFormName => CffFormNumber > 0 && CffFormNumber <= 4 ? $"CFF Form {CffFormNumber}" : $"CFF Form ?";
    public string MissionName => MissionNumber > 0 ? $"Mission {MissionNumber}" : "Mission ?";
    public bool HasTargetListed => TargetPoint != null;
    public bool ShouldDrawOverlay => HasTargetListed;
    public bool IsAimed => Status == "Aimed";
    public bool IsExecuting => Status == "Executing";
    public bool IsRoundsComplete => Status == "RoundsComplete";
    public bool IsTimedExecutionMission => ScheduledExecutionTime.HasValue;
    public bool IsTerminal => Status == "RoundsComplete" || Status == "EndMission" || Status == "NoSolution" || Status == "CheckFire";
    public bool IsPlaceholder =>
        RequestId <= 0
        && BatteryId == 0
        && string.IsNullOrWhiteSpace(BatteryName)
        && string.IsNullOrWhiteSpace(TargetNumber)
        && string.IsNullOrWhiteSpace(TargetLocationText)
        && string.IsNullOrWhiteSpace(Round)
        && NumberOfRounds <= 0
        && MaxOrdinateMSL_m <= 0
        && TimeOfFlight_s <= 0;

    public static CallForFireMissionSnapshot FromMission(
        ICallForFire.CallForFireEventArgs.CallForFireMission mission,
        IMap? map,
        DateTime missionTime,
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
            TimeText = mission.Time ?? "",
            MethodOfControlText = mission.MethodOfControl.ToString(),
            SeadMissionTypeText = mission.SEADMissionType.ToString(),
            ScheduledExecutionTime = ParseScheduledExecutionTime(mission.Time, missionTime, mission.MethodOfControl.ToString()),
            Status = mission.Status.ToString()
        };
    }

    public static DateTime? ParseScheduledExecutionTime(string? timeText, DateTime referenceMissionTime, string? timingMode = null)
    {
        if (string.IsNullOrWhiteSpace(timeText))
        {
            return null;
        }

        if (IsTimeToTargetMode(timingMode)
            && double.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return referenceMissionTime.AddSeconds(seconds);
        }

        if (TimeSpan.TryParse(timeText, CultureInfo.InvariantCulture, out var timeOfDay)
            || TimeSpan.TryParse(timeText, out timeOfDay))
        {
            if (IsTimeToTargetMode(timingMode))
            {
                return referenceMissionTime.Add(timeOfDay);
            }

            return AlignToMissionDay(referenceMissionTime, timeOfDay);
        }

        if (DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDateTime)
            || DateTime.TryParse(timeText, out parsedDateTime))
        {
            if (parsedDateTime.Year <= 1900)
            {
                return null;
            }

            return parsedDateTime;
        }

        return null;
    }

    private static bool IsTimeToTargetMode(string? timingMode)
    {
        if (string.IsNullOrWhiteSpace(timingMode))
        {
            return false;
        }

        var normalized = Normalize(timingMode ?? "");
        return normalized.Contains("TIMETOTARGET")
            || normalized.Contains("TTT");
    }

    private static DateTime AlignToMissionDay(DateTime referenceMissionTime, TimeSpan timeOfDay)
    {
        var candidate = referenceMissionTime.Date.Add(timeOfDay);
        var delta = candidate - referenceMissionTime;
        if (delta > TimeSpan.FromHours(12))
        {
            return candidate.AddDays(-1);
        }

        if (delta < TimeSpan.FromHours(-12))
        {
            return candidate.AddDays(1);
        }

        return candidate;
    }

    private static bool HasToken(string value, string token)
    {
        return Normalize(value).IndexOf(token, StringComparison.Ordinal) >= 0;
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }
}
