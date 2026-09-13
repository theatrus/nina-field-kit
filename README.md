# NINA Field Kit

A proposed collection of small actions, checks, and recovery routines for NINA's Advanced Sequencer.

The first design covers mount health checks and recovery after a communication failure leaves tracking off or the reported position wrong. Other utilities will follow the same rules: explicit intent, bounded waits, clear results, and useful logs.

**Status:** design only. No plugin binaries or working actions are available yet.

Read the [design document](docs/design.md) for the action catalog, mount recovery workflow, Target Scheduler integration, and validation plan.

The initial compatibility target is NINA 3.2. Support for later versions will require a separate build and integration check as needed.
