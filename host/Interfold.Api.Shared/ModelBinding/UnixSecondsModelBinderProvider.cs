using Interfold.Api.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;

namespace Interfold.Api.ModelBinding;

/// <summary>
/// Wires <see cref="UnixSecondsModelBinder"/> into the MVC binder pipeline for
/// <see cref="UnixSeconds"/>-typed action parameters. Registered by inserting an
/// instance at position 0 of <c>MvcOptions.ModelBinderProviders</c> in Program.cs
/// so it takes precedence over the built-in <see cref="SimpleTypeModelBinderProvider"/>
/// / <see cref="ComplexObjectModelBinderProvider"/> fallbacks — otherwise MVC would
/// try to shape <see cref="UnixSeconds"/> as a complex object (looking for a
/// <c>Value</c> constructor argument on the query string) and emit the wrong
/// diagnostic.
/// </summary>
internal sealed class UnixSecondsModelBinderProvider : IModelBinderProvider
{
    private static readonly UnixSecondsModelBinder Instance = new();

    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Metadata.ModelType == typeof(UnixSeconds) ? Instance : null;
    }
}
