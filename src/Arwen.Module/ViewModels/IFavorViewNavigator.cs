namespace Arwen.ViewModels;

/// <summary>
/// Lets a child tab ask Arwen's <c>FavorView</c> to switch tabs and (optionally)
/// hand off context to the destination tab. Implemented by the view that owns
/// the TabControl; injected into VMs that need cross-tab navigation.
/// </summary>
public interface IFavorViewNavigator
{
    /// <summary>Switch to the Gift Scanner tab and select <paramref name="npcKey"/> there.</summary>
    void OpenInGiftScanner(string npcKey);
}

/// <summary>
/// Late-bound forwarder for <see cref="IFavorViewNavigator"/>. Registered as a
/// singleton so VMs can take a stable dependency at construction time, while the
/// real navigator (Arwen's <c>FavorView</c>) is wired in via <see cref="Inner"/>
/// once it finishes building. Without this indirection, injecting <c>FavorView</c>
/// into VMs that <c>FavorView</c> itself constructs creates a DI cycle.
/// </summary>
public sealed class FavorViewNavigatorHolder : IFavorViewNavigator
{
    public IFavorViewNavigator? Inner { get; set; }

    public void OpenInGiftScanner(string npcKey) => Inner?.OpenInGiftScanner(npcKey);
}
