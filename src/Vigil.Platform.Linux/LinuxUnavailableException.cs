namespace Vigil.Platform.Linux;

/// <summary>
/// The session cannot provide what was asked: no display, a missing X11 library, a compositor without an idle source, no
/// bus. The message is written for the user and is shown as it is by <c>vigil diag</c>; the poll loop logs it and tries
/// again at the next poll. It is a normal condition on Linux (the daemon may start before the session's environment is
/// there), not a defect, which is why it is its own type and not a generic failure.
/// </summary>
public sealed class LinuxUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
