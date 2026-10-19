namespace NINA.Plugin.TargetScheduler.Sequencer;

// Match the public Target Scheduler container identity without depending on a plugin installation.
// This checks our ancestor selection and the native synchronous Skip behavior, not the scheduler planner.
internal sealed class TargetSchedulerContainer : NINA.Sequencer.Container.SequentialContainer { }
