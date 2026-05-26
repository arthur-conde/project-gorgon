using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Extracts the active character name from <c>ProcessAddPlayer</c>.
/// Args format: <c>(entityId, arg2, "charId", "CharacterName", x, y, z, heading, ...)</c>
/// — the character name is the 4th positional arg (2nd quoted string).
/// </summary>
internal sealed class Session : IFrameHandler, ISessionState
{
    private readonly IDomainEventPublisher _bus;

    public string? ActiveCharacter { get; private set; }
    public DateTimeOffset? LoggedInAt { get; private set; }

    public Session(IDomainEventPublisher bus) => _bus = bus;

    internal void Reset()
    {
        ActiveCharacter = null;
        LoggedInAt = null;
    }

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        tok.Skip(2); // entityId, arg2
        tok.NextQuotedSpan(); // charId — skip

        var nameSpan = tok.NextQuotedSpan();
        if (nameSpan.IsEmpty)
            return;

        var name = nameSpan.ToString();
        ActiveCharacter = name;
        LoggedInAt = metadata.Timestamp ?? metadata.ReadOn;
        _bus.Publish(new SessionStarted(name, metadata));
    }
}
