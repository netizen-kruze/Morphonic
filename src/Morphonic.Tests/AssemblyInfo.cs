using Xunit;

// Several test classes point AppPaths.DataDir at their own temporary
// folder; running classes in parallel would let one test's folder vanish
// under another. The whole suite takes a few seconds, so it runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
