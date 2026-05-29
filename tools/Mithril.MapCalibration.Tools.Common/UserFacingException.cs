namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Thrown when the tool encounters a user-input or environment problem that
/// should be reported as a clean error message rather than an uncaught
/// stack trace. <c>Program.Main</c> in the CLI catches these and prints them
/// without the trace; WPF consumers surface them in dialogs.
/// </summary>
public sealed class UserFacingException(string message) : Exception(message);
