namespace Mithril.Shared.Reference;

/// <summary>
/// A single rendered row produced by <see cref="EffectDescsRenderer"/>. Views bind to
/// these via the shared <c>EffectLineTemplate</c> in Mithril.Shared/Wpf/Resources.xaml.
/// An <see cref="IconId"/> of 0 means "no icon" (prose rows, or tokens with no IconIds).
/// </summary>
public sealed record EffectLine(int IconId, string Text);
