namespace Mithril.Shared.Logging;

/// <summary>
/// Abstract base for typed parser events. Every module parser returns
/// a concrete subclass — <c>MapTargetDetected</c>, <c>FoodConsumedEvent</c>,
/// etc. <c>Timestamp</c> is the <b>UTC</b> instant the event occurred.
/// </summary>
public abstract record LogEvent(DateTime Timestamp);
