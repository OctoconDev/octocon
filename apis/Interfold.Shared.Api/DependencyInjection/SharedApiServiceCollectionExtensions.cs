using System.Net;
using System.Text.Json;
using Interfold.Shared.Api.ModelBinding;
using Interfold.Shared.Api.Models;
using Interfold.Shared.Api.Services;
using Interfold.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using JsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Interfold.Shared.Api.DependencyInjection;

/// <summary>Shared-spine DI wiring consumed by <c>Program.cs</c> today and by every
/// future <c>Interfold.&lt;Feature&gt;.Api</c> module tomorrow. Order-invariant with
/// <c>AddControllers()</c>: the caller is free to invoke either first.</summary>
public static class SharedApiServiceCollectionExtensions
{
    /// <summary>Registers the shared-spine services: MVC binder / JSON / behaviour option
    /// configurators, and the <see cref="ExceptionHandler"/>. Does <b>not</b> call
    /// <c>AddControllers()</c> — the composition host owns that.</summary>
    public static IServiceCollection AddSharedApi(this IServiceCollection services)
    {
        // UnixSecondsModelBinderProvider at position 0 takes precedence over MVC's SimpleType /
        // ComplexObject providers for UnixSeconds parameters.
        services.Configure<MvcOptions>(mvcOptions =>
        {
            mvcOptions.ModelBinderProviders.Insert(0, new UnixSecondsModelBinderProvider());
        });

        services.Configure<JsonOptions>(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
            options.JsonSerializerOptions.Converters.Add(new UtcDateTimeOffsetConverter());
        });

        // Route DataAnnotations 400s through ErrorResponse so the Kotlin client can decode them
        // (the default ValidationProblemDetails isn't supported). PostConfigure so we win
        // against MvcCoreServiceCollectionExtensions.AddApiExplorer's ApiBehaviorOptionsSetup,
        // which unconditionally assigns InvalidModelStateResponseFactory in its Configure and
        // would otherwise clobber whichever ordering AddControllers is called with.
        services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var firstBadField = context.ModelState
                    .FirstOrDefault(kv => kv.Value?.Errors.Count > 0
                        && !string.IsNullOrWhiteSpace(kv.Value.Errors[0].ErrorMessage));

                var firstError = firstBadField.Value?.Errors[0].ErrorMessage
                    ?? "The request payload was invalid.";

                // Prefer the binder's stashed ErrorCode so invalid_end_anchor / invalid_anchor
                // survive verbatim without needing globally unique messages.
                var stashedCode = context.HttpContext.Items[
                    UnixSecondsBindingAttribute.ItemsKey(firstBadField.Key ?? string.Empty)] as string;

                var code = stashedCode is not null
                    ? new ErrorCode(stashedCode)
                    : ValidationErrorCodeRegistry.LookupOrDefault(firstError);

                var body = new ErrorResponse(firstError, code, HttpStatusCode.BadRequest);
                return new BadRequestObjectResult(body);
            };
        });

        services.AddExceptionHandler<ExceptionHandler>();

        return services;
    }
}
