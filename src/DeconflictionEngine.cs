using System;
using System.Collections.Generic;
using BSI.MACE;

namespace MaceFireAirspace;

internal sealed class AirspaceConflict
{
    public AirspaceConflict(ulong entityId, string entityName, double distanceToVolume_m, double verticalSeparation_ft, double altitudeMsl_m, string severity)
    {
        EntityId = entityId;
        EntityName = entityName;
        DistanceToVolume_m = distanceToVolume_m;
        VerticalSeparation_ft = verticalSeparation_ft;
        AltitudeMsl_m = altitudeMsl_m;
        Severity = severity;
    }

    public ulong EntityId { get; }
    public string EntityName { get; }
    public double DistanceToVolume_m { get; }
    public double VerticalSeparation_ft { get; }
    public double AltitudeMsl_m { get; }
    public string Severity { get; }
}

internal sealed class ConflictRecord
{
    public string Airspace { get; set; } = "";
    public string Aircraft { get; set; } = "";
    public ulong EntityId { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public double DistanceToVolume_m { get; set; }
    public double DistanceToVolume_nm { get; set; }
    public double VerticalSeparation_ft { get; set; }
    public double AltitudeMsl_ft { get; set; }
    public string Severity { get; set; } = "";
}

internal sealed class SeparationSettings
{
    public double Horizontal_nm { get; set; } = 1.0;
    public double Vertical_ft { get; set; } = 1000;
    public double PreFireActivationSeconds { get; set; } = 60;
    public double Horizontal_m => Horizontal_nm * 1852.0;
}

internal static class DeconflictionEngine
{
    private const double MetersToFeet = 3.280839895;

    public static bool IsLikelyAircraft(IPhysicalEntity entity)
    {
        if (!entity.IsActive || entity.IsKilled || entity.Position == null)
        {
            return false;
        }

        var domainText = entity.Domain.ToString();
        return domainText.IndexOf("Air", StringComparison.OrdinalIgnoreCase) >= 0
            || (!entity.IsOnGround && entity.AltitudeMSL_m > 50 && entity.GroundSpeed_mps > 20);
    }

    public static IEnumerable<AirspaceConflict> FindConflicts(AirspaceVolume volume, IEnumerable<IPhysicalEntity> aircraft, SeparationSettings settings)
    {
        foreach (var entity in aircraft)
        {
            var altitude = entity.AltitudeMSL_m;
            var verticalSeparation_m = VerticalSeparationMeters(altitude, volume);
            var distance = AirspaceGeometry.DistanceToVolumeMeters(entity.Position, volume);
            if (distance <= settings.Horizontal_m && verticalSeparation_m * MetersToFeet <= settings.Vertical_ft)
            {
                yield return new AirspaceConflict(
                    entity.ID,
                    entity.Name,
                    distance,
                    verticalSeparation_m * MetersToFeet,
                    altitude,
                    distance <= 0 ? "CONFLICT" : "ADVISORY");
            }
        }
    }

    private static double VerticalSeparationMeters(double altitudeMsl_m, AirspaceVolume volume)
    {
        if (altitudeMsl_m < volume.LowerAltitudeMsl_m)
        {
            return volume.LowerAltitudeMsl_m - altitudeMsl_m;
        }

        if (altitudeMsl_m > volume.UpperAltitudeMsl_m)
        {
            return altitudeMsl_m - volume.UpperAltitudeMsl_m;
        }

        return 0;
    }
}
