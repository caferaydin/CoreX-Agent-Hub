// Loop Zero drives real processes: a Hub executable, its agent process tree, and real git.
// Several of these tests reason about which of those processes exist, so running two of them
// at once would make one test's agent look like another test's orphan. They run one at a
// time on purpose.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
