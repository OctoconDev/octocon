namespace Interfold.Auth.Api.Helpers;

public static class Base64Extensions
{
    public static byte[] Base64UrlDecode(this string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
            break;
            case 3:
                padded += "=";
            break;
        }

        return Convert.FromBase64String(padded);
    }
}
