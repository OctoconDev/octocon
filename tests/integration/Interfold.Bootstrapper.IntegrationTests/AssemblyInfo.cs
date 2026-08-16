using Interfold.Bootstrapper.IntegrationTests.Fixtures;

// Cap concurrency for the whole assembly. See DinDParallelLimit for the reasoning.
[assembly: ParallelLimiter<DinDParallelLimit>]

// Every test class in this assembly carries [Explicit]. The DinD fixtures each cost
// 5–8 GB of transient inner-Docker storage and several minutes of wall-clock (image build,
// pre-pull, publish tarball, systemd bootstrap). A solution-wide local `dotnet test` that
// also spins up the centralised test-bench (see TestBenchCoordinator) and the eleven other
// leaf integration projects previously tipped the disk past 100% and OOM-killed the
// docker daemon; the exclusion keeps the local sanity-check loop fast.
//
// Local: `dotnet test` at the root skips this whole assembly.
// Manual: `dotnet <this.dll> --treenode-filter '/*/*/*/*'` runs every test — the filter
//   matches all tests, all are explicit, so TUnit runs them (see
//   https://tunit.dev/docs/writing-tests/explicit).
// CI: the `test-int` leg for this project invokes the DLL with the same treenode filter
//   so every leg still runs the full matrix.
//
// TUnit does not support an assembly-level [Explicit]; the attribute has to sit on every
// test class. New test classes in this project MUST add [Explicit] at the class level.

