namespace Vigil.Core.Abstractions;

/// <summary>Checks whether the process may post input events. Must never prompt the user.</summary>
public interface IPermissionProbe
{
    bool CanPostEvents();
}
