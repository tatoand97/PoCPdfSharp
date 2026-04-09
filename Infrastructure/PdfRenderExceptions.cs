namespace PoCPdfSharp.Infrastructure;

public sealed class RequestValidationException(string message) : Exception(message);

public sealed class UnprocessableHtmlException(string message) : Exception(message);
