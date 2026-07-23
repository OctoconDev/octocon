using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using OneOf;

namespace Interfold.Api.Models;

public class Response : Response<NoContent>
{
    public Response() : base(new SuccessResponse<NoContent>(new NoContent(), HttpStatusCode.NoContent))
    {
    }
    
    public Response(ErrorResponse response) : base(response)
    {
    }
    
    /// <summary>
    /// Implicitly converts the specified <paramref name="error"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="error">The error to convert.</param>
    public static implicit operator Response(ErrorResponse error)
    {
        return new Response(error);
    }
}

[GenerateOneOf]
public class Response<TValue> : OneOfBase<SuccessResponse<TValue>, ErrorResponse>, IConvertToActionResult
{
    public Response(OneOf<SuccessResponse<TValue>, ErrorResponse> input) : base(input)
    {
    }

    /// <summary>
    /// Implicitly converts the specified <paramref name="value"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    public static implicit operator Response<TValue>(TValue value)
    {
        return new Response<TValue>(new SuccessResponse<TValue>(value));
    }
    
    /// <summary>
    /// Implicitly converts the specified <paramref name="value"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    public static implicit operator Response<TValue>(SuccessResponse<TValue> value)
    {
        return new Response<TValue>(value);
    }
    
    /// <summary>
    /// Implicitly converts the specified <paramref name="error"/> to an <see cref="Response{TValue}"/>.
    /// </summary>
    /// <param name="error">The error to convert.</param>
    public static implicit operator Response<TValue>(ErrorResponse error)
    {
        return new Response<TValue>(error);
    }
    
    public bool IsSuccess => IsT0;
    public SuccessResponse<TValue> AsSuccess => AsT0;
    public bool IsError => IsT1;
    public ErrorResponse AsError => AsT1;

    public IActionResult Convert()
    {
        if (IsSuccess)
        {
            var statusCode = (int)AsSuccess.StatusCode;
            if (statusCode == 204)
                return new StatusCodeResult(204);

            return new ObjectResult(AsSuccess)
            {
                DeclaredType = typeof(SuccessResponse<TValue>),
                StatusCode = statusCode
            };
        }

        return new ObjectResult(AsError)
        {
            DeclaredType = typeof(ErrorResponse),
            StatusCode = (int)AsError.StatusCode
        };
    }
}
