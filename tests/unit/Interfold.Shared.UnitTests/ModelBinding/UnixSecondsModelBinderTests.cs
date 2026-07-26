using System.Globalization;
using System.Reflection;
using Interfold.Shared.Api.ModelBinding;
using Interfold.Shared.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Interfold.Api.UnitTests.ModelBinding;

// UnixSecondsModelBinder wire behaviour: on success → ModelBindingResult.Success; on
// failure → exact attribute ErrorMessage + attribute ErrorCode stashed on HttpContext.Items
// for InvalidModelStateResponseFactory to lift into ErrorResponse.Code. Regressions
// silently downgrade Fronting month/between endpoints to generic `bad_request`.
public sealed class UnixSecondsModelBinderTests
{
    private const string EndAnchorField = "end_anchor";
    private const string EndAnchorMessage = "Invalid end anchor. Please pass a valid Unix timestamp.";
    private const string EndAnchorCode = "invalid_end_anchor";
    private const string StartField = "start";
    private const string StartMessage = "Invalid start anchor. Please pass a valid Unix timestamp.";
    private const string BetweenCode = "invalid_anchor";

    [Test]
    public async Task Bind_ValidUnixSeconds_ProducesSuccessResultWithParsedValue()
    {
        var ctx = BuildContext(
            fieldName: EndAnchorField,
            rawValue: "1700000000",
            parameter: ResolveParameter(nameof(WithEndAnchor), EndAnchorField));

        await new UnixSecondsModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsTrue();
        await Assert.That(ctx.Result.Model).IsEqualTo(new UnixSeconds(1_700_000_000L));
        await Assert.That(ctx.ModelState.ErrorCount).IsEqualTo(0);
        await Assert.That(ctx.HttpContext.Items[UnixSecondsBindingAttribute.ItemsKey(EndAnchorField)]).IsNull();
    }

    [Test]
    public async Task Bind_UnparseableValue_EmitsAttributeMessageAndStashesErrorCode()
    {
        var ctx = BuildContext(
            fieldName: EndAnchorField,
            rawValue: "not-a-number",
            parameter: ResolveParameter(nameof(WithEndAnchor), EndAnchorField));

        await new UnixSecondsModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsFalse();
        await Assert.That(ctx.ModelState.ContainsKey(EndAnchorField)).IsTrue();

        var error = ctx.ModelState[EndAnchorField]!.Errors[0].ErrorMessage;
        await Assert.That(error).IsEqualTo(EndAnchorMessage);

        var stashed = ctx.HttpContext.Items[UnixSecondsBindingAttribute.ItemsKey(EndAnchorField)] as string;
        await Assert.That(stashed).IsEqualTo(EndAnchorCode);
    }

    // Between wires two params to invalid_anchor with per-param messages; pin the start-
    // side wiring in isolation from the end-side.
    [Test]
    public async Task Bind_BetweenStartField_UsesInvalidAnchorCodeFromAttribute()
    {
        var ctx = BuildContext(
            fieldName: StartField,
            rawValue: "abc",
            parameter: ResolveParameter(nameof(WithBetweenAnchors), StartField));

        await new UnixSecondsModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsFalse();
        await Assert.That(ctx.ModelState[StartField]!.Errors[0].ErrorMessage).IsEqualTo(StartMessage);

        var stashed = ctx.HttpContext.Items[UnixSecondsBindingAttribute.ItemsKey(StartField)] as string;
        await Assert.That(stashed).IsEqualTo(BetweenCode);
    }

    // Fronting endpoints require these params; an absent query string must still surface
    // as invalid_end_anchor / invalid_anchor rather than MVC's generic "value is required".
    [Test]
    public async Task Bind_MissingValue_EmitsAttributeMessageAndStashesErrorCodeToo()
    {
        var ctx = BuildContext(
            fieldName: EndAnchorField,
            rawValue: null,
            parameter: ResolveParameter(nameof(WithEndAnchor), EndAnchorField));

        await new UnixSecondsModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsFalse();
        await Assert.That(ctx.ModelState[EndAnchorField]!.Errors[0].ErrorMessage).IsEqualTo(EndAnchorMessage);

        var stashed = ctx.HttpContext.Items[UnixSecondsBindingAttribute.ItemsKey(EndAnchorField)] as string;
        await Assert.That(stashed).IsEqualTo(EndAnchorCode);
    }

    // Defensive fallback for a parameter without [UnixSecondsBinding] — still fails
    // cleanly (registry routes response to `bad_request`).
    [Test]
    public async Task Bind_UnparseableValue_WithoutAttribute_StashesNothing()
    {
        var ctx = BuildContext(
            fieldName: "raw",
            rawValue: "nope",
            parameter: ResolveParameter(nameof(WithoutAttribute), "raw"));

        await new UnixSecondsModelBinder().BindModelAsync(ctx);

        await Assert.That(ctx.Result.IsModelSet).IsFalse();
        await Assert.That(ctx.ModelState["raw"]!.Errors[0].ErrorMessage)
            .IsEqualTo("The value 'nope' is not a valid Unix timestamp.");

        var stashed = ctx.HttpContext.Items[UnixSecondsBindingAttribute.ItemsKey("raw")];
        await Assert.That(stashed).IsNull();
    }

    // Builds the DefaultModelBindingContext MVC would present at request time.
    private static DefaultModelBindingContext BuildContext(
        string fieldName,
        string? rawValue,
        ParameterInfo parameter)
    {
        var metadata = new EmptyModelMetadataProvider().GetMetadataForParameter(parameter);
        var httpContext = new DefaultHttpContext();

        var actionDescriptor = new ActionDescriptor
        {
            Parameters = new List<ParameterDescriptor>
            {
                new ControllerParameterDescriptor
                {
                    Name = parameter.Name!,
                    ParameterInfo = parameter,
                    ParameterType = parameter.ParameterType,
                }
            }
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), actionDescriptor);

        var routeValues = new RouteValueDictionary();
        if (rawValue is not null)
        {
            routeValues[fieldName] = rawValue;
        }

        return new DefaultModelBindingContext
        {
            ActionContext = actionContext,
            ModelName = fieldName,
            FieldName = fieldName,
            ModelMetadata = metadata,
            ModelState = new ModelStateDictionary(),
            ValueProvider = new RouteValueProvider(BindingSource.Query, routeValues, CultureInfo.InvariantCulture),
        };
    }

    private static ParameterInfo ResolveParameter(string methodName, string queryName)
    {
        var method = typeof(UnixSecondsModelBinderTests)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;
        return method.GetParameters().First(p =>
        {
            var fromQuery = p.GetCustomAttribute<FromQueryAttribute>();
            return fromQuery?.Name == queryName || p.Name == queryName;
        });
    }

    // Mirrors FrontingController.Month's parameter shape.
    private static void WithEndAnchor(
        [FromQuery(Name = EndAnchorField)]
        [UnixSecondsBinding(ErrorCode = EndAnchorCode, ErrorMessage = EndAnchorMessage)]
        UnixSeconds endAnchor)
    { _ = endAnchor; }

    // Mirrors FrontingController.Between's parameter shape.
    private static void WithBetweenAnchors(
        [FromQuery(Name = StartField)]
        [UnixSecondsBinding(ErrorCode = BetweenCode, ErrorMessage = StartMessage)]
        UnixSeconds startAnchor,
        [FromQuery(Name = "end")]
        [UnixSecondsBinding(ErrorCode = BetweenCode, ErrorMessage = "Invalid end anchor. Please pass a valid Unix timestamp.")]
        UnixSeconds endAnchor)
    { _ = startAnchor; _ = endAnchor; }

    private static void WithoutAttribute(UnixSeconds raw)
    { _ = raw; }
}
