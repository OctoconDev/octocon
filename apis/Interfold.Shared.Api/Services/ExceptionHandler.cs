using Interfold.Api.Models;
using Interfold.Shared.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace Interfold.Api.Services;

public class ExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Log the exception and return a generic error response.
        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsJsonAsync(CreateError(httpContext, exception), cancellationToken);
        return true;
    }

    private ErrorResponse CreateError(HttpContext httpContext, Exception exception)
    {
        if (exception is InterfoldException interfoldException)
        {
            httpContext.Response.StatusCode = (int?)interfoldException.HttpStatusCode ?? StatusCodes.Status500InternalServerError;
            return new ErrorResponse(interfoldException.Message, interfoldException.Code);
        }
        
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return exception switch
        {
            BadHttpRequestException badRequestException => new ErrorResponse(badRequestException.Message, ErrorCodes.BadRequest),
            _ => new ErrorResponse($"Unhandled exception: {exception}", ErrorCodes.UnknownError)
        };
    }
}
