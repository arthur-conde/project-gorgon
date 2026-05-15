using System;

namespace Mithril.Shared.Wpf.Sorting;

/// <summary>
/// Declarative description of one property a collection can be sorted by.
/// </summary>
/// <param name="Id">Stable identifier; the column name used in <c>ORDER BY</c>.</param>
/// <param name="DisplayName">User-visible label for the chip.</param>
/// <param name="DefaultDescending">Initial direction when first toggled active.</param>
/// <param name="KeySelector">Optional in-memory key extractor for computed values that don't map to a simple property path. When present, the controller registers a <c>ColumnBinding</c> with this selector so <c>ORDER BY Id</c> resolves to it.</param>
public sealed record SortKey<T>(
    string Id,
    string DisplayName,
    bool DefaultDescending = false,
    Func<T, object?>? KeySelector = null);
