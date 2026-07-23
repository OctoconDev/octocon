using TUnit.Core;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>Process-static record of which <see cref="IWebFactoryFixture"/> implementations
/// the filtered test session may request. Populated from
/// <see cref="TestDiscoveryContext.Current"/> at <c>[After(TestDiscovery)]</c>. Assembly-walk
/// fallback fires if the context is null so filter-only runs never miss a needed fixture.</summary>
internal static class RequiredFixtures
{
    private static readonly HashSet<Type> Discovered = new();
    private static readonly object DiscoveryLock = new();
    private static bool _populated;

    public static bool NeedScylla => Has<ScyllaWebFactoryFixture>();

    public static bool NeedCassandra => Has<CassandraWebFactoryFixture>();

    public static bool NeedMultiNodeScylla => Has<MultiNodeScyllaFixture>();

    /// <summary>Driven by <c>[After(TestDiscovery)]</c> on <c>BaseEndpointTest</c>;
    /// idempotent.</summary>
    public static void Discover()
    {
        EnsureDiscovered();
    }

    private static bool Has<TFixture>()
    {
        EnsureDiscovered();
        return Discovered.Contains(typeof(TFixture));
    }

    private static void EnsureDiscovered()
    {
        if (_populated)
        {
            return;
        }

        lock (DiscoveryLock)
        {
            if (_populated)
            {
                return;
            }

            var scheduled = TryGetScheduledClassTypes();
            if (scheduled is not null)
            {
                foreach (var classType in scheduled)
                {
                    foreach (var fixtureType in ExtractClassDataSourceTypes(classType, transitive: true))
                    {
                        Discovered.Add(fixtureType);
                    }
                }

                _populated = true;
                return;
            }

            // Defensive fallback: the scheduled-test contexts weren't populated, so fall back
            // to the legacy assembly walk. This keeps SharedDbFixture from silently running
            // with the wrong toggles if we end up on a TUnit version that changes the
            // discovery lifecycle. The over-eagerness only manifests under filtering, and is
            // exactly the behavior we used to ship.
            DiscoverViaAssemblyWalk();
            _populated = true;
        }
    }

    private static IEnumerable<Type>? TryGetScheduledClassTypes()
    {
        // Prefer the session context (populated alongside discovery) but fall back to the
        // discovery context which is what fires the [After(TestDiscovery)] hook. Both list the
        // *scheduled* tests post-filter once discovery has run.
        var allTests = (IEnumerable<TestContext>?)TestSessionContext.Current?.AllTests
                     ?? TestDiscoveryContext.Current?.AllTests;
        if (allTests is null)
        {
            return null;
        }

        // Materialise once so multiple Linq passes don't re-enumerate the underlying TUnit
        // collection (it isn't documented as multi-iteration safe).
        var snapshot = allTests as IReadOnlyCollection<TestContext> ?? allTests.ToArray();
        if (snapshot.Count == 0)
        {
            // Empty scheduled set still counts as "populated" — honour the filter even when
            // the user explicitly excludes everything.
            return Array.Empty<Type>();
        }

        var classTypes = new HashSet<Type>();
        foreach (var test in snapshot)
        {
            // TestContext exposes test details via Metadata.TestDetails per TUnit.Core 1.56.
            var classType = test.Metadata.TestDetails.ClassType;
            if (classType is not null)
            {
                classTypes.Add(classType);
            }
        }

        return classTypes;
    }

    private static void DiscoverViaAssemblyWalk()
    {
        var assembly = typeof(RequiredFixtures).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var fixtureType in ExtractClassDataSourceTypes(type, transitive: false))
            {
                Discovered.Add(fixtureType);
            }
        }
    }

    /// <summary>Yields the generic args of every <c>ClassDataSourceAttribute&lt;...&gt;</c>
    /// on <paramref name="testClassType"/> (or a base). Transitive walk covers indirect
    /// fixtures (e.g. ScyllaWebFactoryFixture → SharedDbFixture) so filter-only runs still
    /// discover them.</summary>
    private static IEnumerable<Type> ExtractClassDataSourceTypes(Type testClassType, bool transitive)
    {
        var visited = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(testClassType);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var attr in current.GetCustomAttributes(inherit: true))
            {
                var attrType = attr.GetType();
                if (!attrType.IsGenericType)
                {
                    continue;
                }

                var openGeneric = attrType.GetGenericTypeDefinition();
                if (openGeneric.FullName?.StartsWith(
                        "TUnit.Core.ClassDataSourceAttribute`",
                        StringComparison.Ordinal) != true)
                {
                    continue;
                }

                foreach (var t in attrType.GetGenericArguments())
                {
                    yield return t;
                    if (transitive)
                    {
                        stack.Push(t);
                    }
                }
            }
        }
    }
}
