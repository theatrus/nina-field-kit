Field Kit 0.1.0.17, for NINA 3.2.0.9001 or newer.

**Cloud guard** watches comparable light-frame star counts, sustained guiding loss, guide-star instability and failed native sequence plate solves. Add it to a parent imaging container's **Triggers**, above your targets or Target Scheduler container.

Choose **Park and retry** to stop guiding and park at the next light-exposure boundary, wait, then recenter and resume. Or choose **Report unsafe** and enable Cloud guard in Field Kit Safety Monitor to use your existing unsafe handler. The retry timer defaults to five minutes; it permits a new imaging trial rather than measuring clear skies. The episode budget and morning cutoff stop prolonged recovery. See the [Cloud guard guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.17/docs/cloud-guard.md) for setup, defaults and limitations.

Safety Monitor Setup also accepts **local ASCOM safety monitors** alongside Alpaca sources. Existing device selections continue to work under the shorter **Field Kit Safety Monitor** name. Cloud integration is optional and disabled by default.

Includes the corrected native editor for **Sky flats at captured rotations**. Tests cover detector timing, NINA event bridges, safety aggregation, cancellation, recovery gates, ASCOM threading/timeouts and themed UI. Physical cloud recovery and a real Target Scheduler session still need equipment validation. Published DLLs are code-signed.

Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
