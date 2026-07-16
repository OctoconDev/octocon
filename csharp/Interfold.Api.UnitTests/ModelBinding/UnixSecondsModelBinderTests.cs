using System.Globalization;
using System.Reflection;
using Interfold.Api.ModelBinding;
using Interfold.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Interfold.Api.UnitTests.ModelBinding;

/// <summary>
/// Pins the wire-visible behaviour of <see cref="UnixSecondsModelBinder"/>: on a
/// successful parse the binding lands as <see cref="ModelBindingResult.Success"/>
/// with a populated <see cref="UnixSeconds"/>, on failure the binder emits the
/// exact <see cref="UnixSecondsBindingAttribute.ErrorMessage"/> from the parameter
/// as a <see cref="ModelError"/> AND stashes the accompanying
/// <see cref="UnixSecondsBindingAttribute.ErrorCode"/> on
/// <see cref="HttpContext.Items"/> so <c>InvalidModelStateResponseFactory</c> in
/// Program.cs can lift it into the final <c>ErrorResponse.Code</c>. If either half
/// of that contract regresses the Fronting <c>month</c> / <c>between</c> endpoints
/// silently downgrade to the generic <c>bad_request</c> code and the Kotlin client
/// stops matching on <c>invalid_end_anchor</c> / <c>invalid_anchor</c>.
/// </summary>
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

    [Test]
    public async Task Bind_BetweenStartField_UsesInvalidAnchorCodeFromAttribute()
    {
        // Between wires two parameters to code = invalid_anchor with per-parameter
        // messages; this locks the start-side wiring in isolation from the end-side.
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

    [Test]
    public async Task Bind_MissingValue_EmitsAttributeMessageAndStashesErrorCodeToo()
    {
        // Fronting endpoints treat these params as required — no default. When the
        // query string is absent altogether we still want the invalid_end_anchor /
        // invalid_anchor wire shape rather than MVC's "value is required" generic.
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

    [Test]
    public async Task Bind_UnparseableValue_WithoutAttribute_StashesNothing()
    {
        // Defensive fallback for a parameter without [UnixSecondsBinding] — should
        // still fail cleanly (registry then routes the response to `bad_request`).
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

    /// <summary>
    /// Builds a <see cref="DefaultModelBindingContext"/> wired the way MVC would
    /// present it to the binder at request time: parameter-level
    /// <see cref="ModelMetadata"/> (so <c>.Attributes.Attributes</c> carries the
    /// <see cref="UnixSecondsBindingAttribute"/>), a live <see cref="HttpContext"/>
    /// on the ActionContext (the binder writes to <c>HttpContext.Items</c>), and a
    /// <see cref="ControllerParameterDescriptor"/> in the ActionDescriptor so the
    /// reflection fallback in the binder resolves as well.
    /// </summary>
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

    // Test-only method that mirrors FrontingController.Month's parameter shape so the
    // ParameterInfo reflection carries the real production attributes.
    private static void WithEndAnchor(
        [FromQuery(Name = EndAnchorField)]
        [UnixSecondsBinding(ErrorCode = EndAnchorCode, ErrorMessage = EndAnchorMessage)]
        UnixSeconds endAnchor)
    { _ = endAnchor; }

    // Test-only method that mirrors FrontingController.Between's parameter shape.
    private static void WithBetweenAnchors(
        [FromQuery(Name = StartField)]
        [UnixSecondsBinding(ErrorCode = BetweenCode, ErrorMessage = StartMessage)]
        UnixSeconds startAnchor,
        [FromQuery(Name = "end")]
        [UnixSecondsBinding(ErrorCode = BetweenCode, ErrorMessage = "Invalid end anchor. Please pass a valid Unix timestamp.")]
        UnixSeconds endAnchor)
    { _ = startAnchor; _ = endAnchor; }

    // Defensive-fallback case: no [UnixSecondsBinding], no [FromQuery(Name=...)].
    private static void WithoutAttribute(UnixSeconds raw)
    { _ = raw; }
}
