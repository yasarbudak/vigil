namespace Vigil.Core.Abstractions;

/// <summary>Outcome of posting one input event. Posting is not proof of effect; the engine verifies separately.</summary>
public readonly record struct InjectionResult(bool Posted, string? Error = null)
{
    public static InjectionResult Success { get; } = new(true);

    public static InjectionResult Failure(string error) => new(false, error);
}
