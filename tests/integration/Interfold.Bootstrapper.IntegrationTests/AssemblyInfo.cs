using Interfold.Bootstrapper.IntegrationTests.Fixtures;

// Cap concurrency for the whole assembly. See DinDParallelLimit for the reasoning.
[assembly: ParallelLimiter<DinDParallelLimit>]

