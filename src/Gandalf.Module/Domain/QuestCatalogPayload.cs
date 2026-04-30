using Mithril.Shared.Reference;

namespace Gandalf.Domain;

/// <summary>
/// Source-metadata payload attached to every quest <see cref="TimerCatalogEntry"/>
/// so consumers can reach the underlying <see cref="QuestEntry"/> without
/// re-querying <c>IReferenceDataService</c>. Lets the Quests-tab VM render
/// FavorNpc / Keywords without coupling the renderer to the reference layer.
/// </summary>
public sealed record QuestCatalogPayload(QuestEntry Quest);
