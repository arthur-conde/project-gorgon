namespace Mithril.Overlay;

/// <summary>
/// Opaque style handle attached to a world-coord marker. Each consumer
/// (Legolas, Gwaihir, …) registers its own concrete style types at startup;
/// the overlay holds them opaquely and the renderer dispatches on the
/// concrete style type. Legolas would register e.g. <c>SurveyPinStyle</c> /
/// <c>MotherlodePinStyle</c> / <c>PlayerMarkerStyle</c>; Gwaihir would
/// register <c>PoiPinStyle</c>; etc.
///
/// <para>The interface deliberately carries no members &#8212; v1 dispatches
/// on <c>style.GetType()</c> inside <c>MarkerSceneRenderer</c>. If a third
/// consumer arrives and a registry interface becomes warranted, the
/// promotion is forward-compat (existing implementers still satisfy the
/// empty contract).</para>
/// </summary>
public interface IMarkerStyle
{
}
