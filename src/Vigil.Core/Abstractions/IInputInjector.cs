namespace Vigil.Core.Abstractions;

/// <summary>Posts one invisible input event that resets the OS idle counter.</summary>
public interface IInputInjector
{
    InjectionResult Inject();
}
