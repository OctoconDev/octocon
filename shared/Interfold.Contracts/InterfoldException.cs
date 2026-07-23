using System.Net;

namespace Interfold.Contracts;

public class InterfoldException : Exception
{
    public InterfoldException(string message, ErrorCode code, HttpStatusCode? httpStatusCode = null) : base(message)
    {
        Code = code;
        HttpStatusCode = httpStatusCode;
    }

    public InterfoldException(string message, ErrorCode code, Exception innerException, HttpStatusCode? httpStatusCode = null) : base(message, innerException)
    {
        Code = code;
        HttpStatusCode = httpStatusCode;
    }

    public ErrorCode Code { get; }

    public HttpStatusCode? HttpStatusCode { get; set; }
}