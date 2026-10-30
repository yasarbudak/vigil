using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux;

/// <summary>One line of the platform section of <c>vigil diag</c>.</summary>
public readonly record struct LinuxDetail(string Label, string Value);

/// <summary>
/// One way of posting input on Linux (X11 XTest, the desktop portal, uinput): the injector, the permission probe that never
/// prompts, and the requester that may. The three backends differ in what "permission" means, so each one answers for itself.
/// </summary>
internal interface IInputBackend : IInputInjector, IPermissionProbe, IPermissionRequester, IDisposable
{
    /// <summary>A short name for the status line, for example "X11 (XTest)".</summary>
    string Name { get; }

    /// <summary>What to tell the user after <see cref="IPermissionRequester.RequestAccess"/> returned false.</summary>
    string NotGrantedHint { get; }

    /// <summary>Facts for <c>vigil diag</c>. Read only: never posts input, never shows a prompt.</summary>
    IReadOnlyList<LinuxDetail> Diagnose();
}
