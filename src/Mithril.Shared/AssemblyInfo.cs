using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mithril.Shared.Tests")]
// Mithril.WorldSim.Chat needs ChatLogClock + LogSourceTailer (both internal)
// to drive its replay-aware chat source — the world-sim layer is the second
// in-repo consumer of those primitives, after Mithril.Shared's own L1
// pipeline. Exposing them broadly would invite reuse for the wrong reasons
// (their semantics are subtle); InternalsVisibleTo to one downstream is the
// surgical scope.
[assembly: InternalsVisibleTo("Mithril.WorldSim.Chat")]
[assembly: InternalsVisibleTo("Mithril.WorldSim.Chat.Tests")]
