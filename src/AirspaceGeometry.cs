using System;
using System.Collections.Generic;
using BSI.MACE;

namespace MaceFireAirspace;

internal static class AirspaceGeometry
{
    private const double EarthRadius_m = 6371008.8;
    private const double CorridorHalfWidth_m = 304.8;
    private const double EndpointRadius_m = 1000;
    private const double DefaultUpperMsl_m = 6000;

    public static AirspaceVolume BuildVolume(
        CallForFireMissionSnapshot? mission,
        IPhysicalEntity firingEntity,
        IGeoPoint target,
        DateTime missionTime,
        TimeSpan postFireBuffer,
        bool isExecuted)
    {
        var gunPoint = firingEntity.Position.Clone();
        var targetPoint = target.Clone();
        var tof = mission?.TimeOfFlight_s > 0 ? mission.TimeOfFlight_s : 60;
        var upper = mission?.MaxOrdinateMSL_m > 0 ? mission.MaxOrdinateMSL_m : Math.Max(DefaultUpperMsl_m, targetPoint.AltitudeMSL_meters + 1500);
        var width = CorridorHalfWidth_m;
        var bearing = mission?.GunTargetLine_deg > 0
            ? mission.GunTargetLine_deg
            : BearingDegrees(gunPoint, targetPoint);

        var leftBearing = NormalizeDegrees(bearing - 90);
        var rightBearing = NormalizeDegrees(bearing + 90);

        var polygon = new List<IGeoPoint>
        {
            Move(gunPoint, leftBearing, width),
            Move(targetPoint, leftBearing, width),
            Move(targetPoint, rightBearing, width),
            Move(gunPoint, rightBearing, width)
        };

        var request = mission?.RequestId.ToString() ?? "Unmatched";
        var formName = mission?.CffFormName ?? "Unmatched CFF";
        var missionName = mission?.MissionName ?? "Unmatched Mission";
        var volume = new AirspaceVolume
        {
            SourceKey = mission != null ? $"cff:{mission.RequestId}" : $"gun:{firingEntity.ID}:{targetPoint.GeohashAsString}",
            DisplayName = $"{formName} / {missionName}",
            RequestId = mission?.RequestId ?? 0,
            CffFormName = formName,
            MissionName = missionName,
            FiringEntityId = firingEntity.ID,
            FiringEntityName = mission?.BatteryName ?? GetEntityCallsign(firingEntity),
            GunPoint = gunPoint,
            TargetPoint = targetPoint,
            StartTime = missionTime,
            NotBefore = missionTime.AddSeconds(tof).Add(postFireBuffer),
            IsExecuted = isExecuted,
            LowerAltitudeMsl_m = Math.Min(gunPoint.AltitudeMSL_meters, targetPoint.AltitudeMSL_meters),
            UpperAltitudeMsl_m = upper,
            LateralBuffer_m = width
        }.WithPolygon(polygon);

        volume.FootprintPolygons.Add(BuildCircle(gunPoint, EndpointRadius_m, 36));
        volume.FootprintPolygons.Add(BuildCircle(targetPoint, EndpointRadius_m, 36));
        return volume;
    }

    public static IGeoPoint Midpoint(IGeoPoint a, IGeoPoint b)
    {
        return new GeoPoint(
            (a.Latitude_degrees + b.Latitude_degrees) / 2.0,
            (a.Longitude_degrees + b.Longitude_degrees) / 2.0,
            (a.AltitudeMSL_meters + b.AltitudeMSL_meters) / 2.0);
    }

    public static IGeoPoint Move(IGeoPoint point, double bearing_deg, double distance_m)
    {
        var clone = point.Clone();
        clone.MoveOnRadialByDeg(distance_m, bearing_deg);
        return clone;
    }

    public static double DistanceMeters(IGeoPoint a, IGeoPoint b)
    {
        var lat1 = ToRad(a.Latitude_degrees);
        var lat2 = ToRad(b.Latitude_degrees);
        var dLat = ToRad(b.Latitude_degrees - a.Latitude_degrees);
        var dLon = ToRad(b.Longitude_degrees - a.Longitude_degrees);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadius_m * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public static double DistanceToPolygonMeters(IGeoPoint point, IReadOnlyList<IGeoPoint> polygon)
    {
        if (polygon.Count < 3)
        {
            return double.MaxValue;
        }

        var origin = polygon[0];
        var p = Project(point, origin);
        var points = new List<(double X, double Y)>();
        foreach (var vertex in polygon)
        {
            points.Add(Project(vertex, origin));
        }

        if (IsInside(p, points))
        {
            return 0;
        }

        var min = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            min = Math.Min(min, DistancePointToSegment(p, a, b));
        }

        return min;
    }

    public static double DistanceToVolumeMeters(IGeoPoint point, AirspaceVolume volume)
    {
        var min = DistanceToPolygonMeters(point, volume.Polygon);
        foreach (var polygon in volume.FootprintPolygons)
        {
            min = Math.Min(min, DistanceToPolygonMeters(point, polygon));
        }

        return min;
    }

    private static AirspaceVolume WithPolygon(this AirspaceVolume volume, IEnumerable<IGeoPoint> polygon)
    {
        volume.Polygon.AddRange(polygon);
        return volume;
    }

    private static List<IGeoPoint> BuildCircle(IGeoPoint center, double radius_m, int segments)
    {
        var points = new List<IGeoPoint>();
        for (var i = 0; i < segments; i++)
        {
            points.Add(Move(center, i * 360.0 / segments, radius_m));
        }

        return points;
    }

    private static string GetEntityCallsign(IPhysicalEntity entity)
    {
        return string.IsNullOrWhiteSpace(entity.Label) ? entity.Name : entity.Label;
    }

    private static double BearingDegrees(IGeoPoint from, IGeoPoint to)
    {
        var lat1 = ToRad(from.Latitude_degrees);
        var lat2 = ToRad(to.Latitude_degrees);
        var dLon = ToRad(to.Longitude_degrees - from.Longitude_degrees);
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return NormalizeDegrees(ToDeg(Math.Atan2(y, x)));
    }

    private static (double X, double Y) Project(IGeoPoint point, IGeoPoint origin)
    {
        var lat = ToRad(point.Latitude_degrees);
        var lon = ToRad(point.Longitude_degrees);
        var originLat = ToRad(origin.Latitude_degrees);
        var originLon = ToRad(origin.Longitude_degrees);
        return ((lon - originLon) * Math.Cos(originLat) * EarthRadius_m, (lat - originLat) * EarthRadius_m);
    }

    private static bool IsInside((double X, double Y) p, IReadOnlyList<(double X, double Y)> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if (((pi.Y > p.Y) != (pj.Y > p.Y)) && p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double DistancePointToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
        {
            return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2));
        }

        var t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy)));
        var x = a.X + t * dx;
        var y = a.Y + t * dy;
        return Math.Sqrt(Math.Pow(p.X - x, 2) + Math.Pow(p.Y - y, 2));
    }

    private static double NormalizeDegrees(double value)
    {
        value %= 360;
        return value < 0 ? value + 360 : value;
    }

    private static double ToRad(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDeg(double radians) => radians * 180.0 / Math.PI;
}
