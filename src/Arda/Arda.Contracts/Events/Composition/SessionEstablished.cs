namespace Arda.Composition;

/// <summary>
/// Published when the <see cref="SessionComposer"/> has assembled enough
/// cross-source data to identify the current game session.
/// </summary>
public readonly record struct SessionEstablished(ComposedSession Session);
