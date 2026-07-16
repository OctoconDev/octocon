using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// Region resolution for the multi-region persistence topology. Resolution is typed as
/// <see cref="ScyllaKeyspace"/>; callers unwrap to the lowercase wire name (via
/// <c>ToWireValue()</c>) only at CQL keyspace interpolation and system-key/prefix
/// construction. The <c>nam:</c> prefix inside <see cref="SystemId.Value"/> stays a
/// string concern owned by <see cref="ScopedSystemId"/> (whose
/// <see cref="ScopedSystemId.StripRegionPrefix(SystemId)"/> helpers are the single source
/// of truth for the strip). Only a <see cref="SystemId"/> overload is exposed; production
/// ingress (JWT middleware, route binding) constructs the wrapper at the trust boundary
/// so no caller has a bare string in hand here.
/// </summary>
public interface IRegionContext
{
    ScyllaKeyspace CurrentRegion { get; }

    ScyllaKeyspace ResolveUserRegion(SystemId systemId);
}
