// SPDX-License-Identifier: MPL-2.0
// Adapted from photon1503/SkyFlats, including theatrus PR #4.
// Source and modifications: see THIRD-PARTY-NOTICES.md.
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using System.Threading;
using System.Threading.Tasks;

namespace Nina.FieldKit.Plugin.SkyFlats {
    public static class NullPointSlew {
        public static async Task ExecuteAsync(ITelescopeMediator telescopeMediator,
            TopocentricCoordinates coordinates, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            var info = telescopeMediator.GetInfo();
            if (!info.Connected) {
                throw new SequenceEntityFailedException("Null-point slew requires a connected mount.");
            }
            if (info.AtPark) {
                throw new SequenceEntityFailedException("Unpark the mount before slewing to the null point.");
            }

            Logger.Info($"Null-point slew: {coordinates}; CanSlewAltAz={info.CanSlewAltAz}");
            bool success;
            if (info.CanSlewAltAz) {
                success = await telescopeMediator.SlewToTopocentricCoordinates(coordinates, token);
            } else {
                // This overload converts Alt/Az to RA/Dec, as in NINA's built-in Alt/Az instruction.
                Logger.Info("Null-point slew uses the RA/Dec fallback.");
                success = await telescopeMediator.SlewToCoordinatesAsync(coordinates, token);
            }

            token.ThrowIfCancellationRequested();
            if (!success) {
                throw new SequenceEntityFailedException("NINA reported a failed null-point slew.");
            }
            if (!telescopeMediator.SetTrackingEnabled(true)) {
                throw new SequenceEntityFailedException("Could not enable tracking after the null-point slew.");
            }
        }
    }
}
