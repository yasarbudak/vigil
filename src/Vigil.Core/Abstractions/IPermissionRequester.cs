namespace Vigil.Core.Abstractions;

/// <summary>Asks the OS for permission to post input events. May prompt the user, so it is only used on explicit request.</summary>
public interface IPermissionRequester
{
    /// <returns>True when the permission is already granted.</returns>
    bool RequestAccess();
}
