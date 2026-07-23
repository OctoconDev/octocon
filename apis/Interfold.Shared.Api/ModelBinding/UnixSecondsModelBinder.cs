using Interfold.Api.Models;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace Interfold.Api.ModelBinding;

/// <summary>
/// MVC binder for <see cref="UnixSeconds"/> query anchors. Reads the raw string from
/// the field's value provider, calls
/// <see cref="UnixSeconds.TryParse(string?, IFormatProvider?, out UnixSeconds)"/>,
/// and on failure adds a <see cref="ModelError"/> whose message is taken verbatim
/// from the parameter's <see cref="UnixSecondsBindingAttribute.ErrorMessage"/> and
/// stashes <see cref="UnixSecondsBindingAttribute.ErrorCode"/> on
/// <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> under
/// <see cref="UnixSecondsBindingAttribute.ItemsKey(string)"/>. The shared
/// <c>InvalidModelStateResponseFactory</c> (Program.cs) prefers that stashed code
/// when producing the final <c>ErrorResponse</c>, preserving the
/// <c>invalid_end_anchor</c> / <c>invalid_anchor</c> wire codes exactly.
///
/// <para>
/// When the attribute is missing (defensive — should not happen in production wiring)
/// the binder still fails cleanly with the raw value in the message and stashes
/// nothing, letting <c>ValidationErrorCodeRegistry</c> route the response to the
/// generic <c>bad_request</c> code.
/// </para>
/// </summary>
internal sealed class UnixSecondsModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var valueProviderResult = bindingContext.ValueProvider.GetValue(bindingContext.FieldName);
        if (valueProviderResult == ValueProviderResult.None)
        {
            var attribute = ResolveAttribute(bindingContext);
            if (attribute is not null)
            {
                bindingContext.ModelState.TryAddModelError(bindingContext.FieldName, attribute.ErrorMessage);
                bindingContext.HttpContext.Items[UnixSecondsBindingAttribute.ItemsKey(bindingContext.FieldName)]
                    = attribute.ErrorCode;
            }
            else
            {
                bindingContext.ModelState.TryAddModelError(
                    bindingContext.FieldName,
                    $"A value for '{bindingContext.FieldName}' is required.");
            }

            bindingContext.Result = ModelBindingResult.Failed();
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.FieldName, valueProviderResult);
        var raw = valueProviderResult.FirstValue;

        if (UnixSeconds.TryParse(raw, provider: null, out var parsed))
        {
            bindingContext.Result = ModelBindingResult.Success(parsed);
            return Task.CompletedTask;
        }

        var failureAttribute = ResolveAttribute(bindingContext);
        if (failureAttribute is not null)
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.FieldName, failureAttribute.ErrorMessage);
            bindingContext.HttpContext.Items[UnixSecondsBindingAttribute.ItemsKey(bindingContext.FieldName)]
                = failureAttribute.ErrorCode;
        }
        else
        {
            bindingContext.ModelState.TryAddModelError(
                bindingContext.FieldName,
                $"The value '{raw}' is not a valid Unix timestamp.");
        }

        bindingContext.Result = ModelBindingResult.Failed();
        return Task.CompletedTask;
    }

    private static UnixSecondsBindingAttribute? ResolveAttribute(ModelBindingContext bindingContext)
    {
        if (bindingContext.ModelMetadata is DefaultModelMetadata defaultMetadata)
        {
            var fromMetadata = defaultMetadata.Attributes.Attributes
                .OfType<UnixSecondsBindingAttribute>()
                .FirstOrDefault();
            if (fromMetadata is not null)
            {
                return fromMetadata;
            }
        }

        var parameter = bindingContext.ActionContext.ActionDescriptor.Parameters
            .OfType<ControllerParameterDescriptor>()
            .FirstOrDefault(p => string.Equals(p.Name, bindingContext.ModelMetadata.ParameterName, StringComparison.Ordinal)
                || string.Equals(p.Name, bindingContext.FieldName, StringComparison.Ordinal));

        return parameter?.ParameterInfo
            .GetCustomAttributes(typeof(UnixSecondsBindingAttribute), inherit: false)
            .OfType<UnixSecondsBindingAttribute>()
            .FirstOrDefault();
    }
}
