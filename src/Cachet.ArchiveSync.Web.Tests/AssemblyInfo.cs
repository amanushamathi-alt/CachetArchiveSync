// Timing-sensitive bUnit tests (poll timers, async modals) intermittently fail when test classes run in
// parallel and compete for CPU; they pass reliably when run serially. Run this assembly serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
