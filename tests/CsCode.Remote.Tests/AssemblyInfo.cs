using Xunit;

// The integration tests start real server processes (and an sshd), count processes and threads, and measure caller-side costs: run one at a time
// on a machine shared with other agents.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
