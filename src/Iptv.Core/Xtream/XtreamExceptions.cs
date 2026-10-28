using System;

namespace Iptv.Core.Xtream;

/// <summary>
/// Base exception for Xtream Codes protocol and communication errors.
/// </summary>
public class XtreamException : Exception
{
    public XtreamException(string message) : base(message) { }
    public XtreamException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when Xtream authentication fails or the account is expired/disabled.
/// </summary>
public sealed class XtreamAuthException : XtreamException
{
    public XtreamAuthException(string message) : base(message) { }
    public XtreamAuthException(string message, Exception innerException) : base(message, innerException) { }
}
