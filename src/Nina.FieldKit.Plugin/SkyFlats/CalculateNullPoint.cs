// SPDX-License-Identifier: MPL-2.0
// Adapted from photon1503/SkyFlats. See THIRD-PARTY-NOTICES.md.
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Profile.Interfaces;

namespace Nina.FieldKit.Plugin.SkyFlats;

public sealed class CalculateNullPoint(IProfileService profileService) {
    // An optional Sun azimuth isolates NINA's native ephemeris dependency in tests.
    public InputTopocentricCoordinates Calculate(double? sunAzimuth = null) {
        var location = profileService.ActiveProfile.AstrometrySettings;
        var point = new InputTopocentricCoordinates(Angle.ByDegree(location.Latitude),
            Angle.ByDegree(location.Longitude), location.Elevation);
        double azimuth;
        if (sunAzimuth is double supplied) {
            azimuth = supplied;
        } else {
            var now = DateTime.UtcNow;
            var observer = new ObserverInfo {
                Latitude = location.Latitude, Longitude = location.Longitude, Elevation = location.Elevation
            };
            var sun = AstroUtil.GetSunPosition(now, AstroUtil.GetJulianDate(now), observer);
            var siderealTime = AstroUtil.GetLocalSiderealTime(now, observer.Longitude);
            var hourAngle = AstroUtil.HoursToDegrees(AstroUtil.GetHourAngle(siderealTime, sun.RA));
            var altitude = AstroUtil.GetAltitude(hourAngle, observer.Latitude, sun.Dec);
            azimuth = AstroUtil.GetAzimuth(hourAngle, altitude, observer.Latitude, sun.Dec);
        }
        if (!double.IsFinite(azimuth)) throw new SequenceEntityFailedException("Could not calculate the Sun's azimuth.");
        var opposite = ((azimuth + 180) % 360 + 360) % 360;
        point.AzDegrees = (int)opposite;
        var minutes = (opposite - point.AzDegrees) * 60;
        point.AzMinutes = (int)minutes;
        point.AzSeconds = (minutes - point.AzMinutes) * 60;
        point.AltDegrees = 75;
        return point;
    }
}
