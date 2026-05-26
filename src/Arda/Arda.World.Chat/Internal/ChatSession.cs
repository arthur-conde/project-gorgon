using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Chat.Events;

namespace Arda.World.Chat.Internal;

/// <summary>
/// Handles <c>CHAT_LOGIN_BANNER</c> — extracts character, server, and timezone
/// offset from the login banner line.
/// <para>
/// Format: <c>**** Logged In As {char}. Server {server}. Timezone Offset {offset}.</c>
/// (with variable-length star prefix).
/// </para>
/// </summary>
internal sealed class ChatSession : IFrameHandler, IChatSessionState
{
    private const string LoggedInAs = "Logged In As ";
    private const string ServerMarker = ". Server ";
    private const string TzMarker = ". Timezone Offset ";

    private readonly IDomainEventPublisher _bus;

    public string? Character { get; private set; }
    public string? Server { get; private set; }
    public TimeSpan? TimezoneOffset { get; private set; }

    public ChatSession(IDomainEventPublisher bus) => _bus = bus;

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        // args is the full line for chat verbs: "**** Logged In As Emraell. Server Laeth. ..."
        var loginIdx = args.IndexOf(LoggedInAs);
        if (loginIdx < 0) return;

        var afterLogin = args[(loginIdx + LoggedInAs.Length)..];

        // Character: up to first '.'
        var charEnd = afterLogin.IndexOf('.');
        if (charEnd <= 0) return;
        var character = afterLogin[..charEnd].ToString();

        // Server: after ". Server " up to next '.'
        var serverIdx = afterLogin.IndexOf(ServerMarker);
        if (serverIdx < 0) return;
        var afterServer = afterLogin[(serverIdx + ServerMarker.Length)..];
        var serverEnd = afterServer.IndexOf('.');
        if (serverEnd <= 0) return;
        var server = afterServer[..serverEnd].ToString();

        // Timezone offset: after ". Timezone Offset " up to '.'
        var tzIdx = afterLogin.IndexOf(TzMarker);
        TimeSpan offset = default;
        if (tzIdx >= 0)
        {
            var afterTz = afterLogin[(tzIdx + TzMarker.Length)..];
            var tzEnd = afterTz.IndexOf('.');
            var tzSpan = tzEnd >= 0 ? afterTz[..tzEnd] : afterTz;
            TimeSpan.TryParse(tzSpan, out offset);
        }

        Character = character;
        Server = server;
        TimezoneOffset = offset;

        _bus.Publish(new ChatSessionIdentified(character, server, offset, metadata));
    }
}
