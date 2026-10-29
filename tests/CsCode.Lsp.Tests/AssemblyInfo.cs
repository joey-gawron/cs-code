using Xunit;

// Timing-sensitive assertions (enqueue cost, breaker/timeout behaviour, latency budgets) and thread/handle counting need a quiet
// process, and the machine is shared with other agents: run the tests one at a time. (Each test is fast; the suite stays well under a minute.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
