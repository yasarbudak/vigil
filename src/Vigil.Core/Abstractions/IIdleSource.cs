namespace Vigil.Core.Abstractions;

/// <summary>Reads how long the user has been idle according to the operating system.</summary>
public interface IIdleSource
{
    /// <summary>Time elapsed since the last real or synthetic input event.</summary>
    TimeSpan GetIdleTime();
}
