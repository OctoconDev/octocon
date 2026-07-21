using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

// Cap concurrency for the whole assembly. See DinDParallelLimit for the reasoning.
[assembly: ParallelLimiter<DinDParallelLimit>]

