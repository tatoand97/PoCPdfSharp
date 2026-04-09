namespace PoCPdfSharp.Infrastructure;

internal static class ExceptionLoggingExtensions
{
    private const string LoggedKey = "PoCPdfSharp.ExceptionAlreadyLogged";

    public static bool HasBeenLogged(this Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.Data.Contains(LoggedKey);
    }

    public static void MarkAsLogged(this Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        exception.Data[LoggedKey] = true;
    }
}
