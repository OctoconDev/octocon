namespace Interfold.Api.Controllers;

/// <summary>
/// Query-parameter names on the fronting history endpoints. Wire-frozen (client-built
/// URIs); const because they feed <c>[FromQuery(Name = …)]</c> attribute arguments.
/// </summary>
internal static class FrontingQueryKeys
{
    public const string EndAnchor = "end_anchor";
    public const string Start = "start";
    public const string End = "end";
}
