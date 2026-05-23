using FluentAssertions;
using Mithril.GameState.Servers;
using Mithril.GameState.Sessions;
using Mithril.Shared.Diagnostics;
using Mithril.WorldSim.Chat;
using Xunit;

namespace Mithril.GameState.Tests.Sessions;

public sealed class SessionAgreementComposerTests
{
    private static readonly ServerEntry Laeth = new(
        "s4", "Laeth", "s4.projectgorgon.com", 9002, "Laeth desc");
    private static readonly ServerEntry Arisetsu = new(
        "s0", "Arisetsu", "s0.projectgorgon.com", 9002, "Arisetsu desc");

    [Fact]
    public void Disagreement_emits_warn_with_SessionAgreement_category_carrying_both_identities_and_session_id()
    {
        // Player.log says Laeth; chat says Arisetsu for the same character —
        // the canonical disagreement case from #633 spec.
        var player = new FakePlayerSessionService(
            new GameSession(
                SessionId: "session-1",
                CharacterName: "Emraell",
                LoggedInUtc: new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: Laeth));
        var chat = new FakeChatSessionService(
            new ChatSession(
                Server: "Arisetsu",
                Character: "Emraell",
                At: new DateTimeOffset(2026, 5, 22, 12, 0, 5, TimeSpan.Zero),
                Offset: TimeSpan.Zero));
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();

            diag.Entries.Should().ContainSingle(
                e => e.Level == DiagnosticLevel.Warn
                  && e.Category == SessionAgreementComposer.DiagnosticCategory);
            var entry = diag.Entries.Single(e => e.Level == DiagnosticLevel.Warn);
            entry.Message.Should().Contain("Laeth");
            entry.Message.Should().Contain("Arisetsu");
            entry.Message.Should().Contain("session-1");
            entry.Message.Should().Contain("Emraell");
            composer.Count.Should().Be(1);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Disagreement_raises_Changed_so_attention_aggregator_surfaces_the_mismatch()
    {
        var player = new FakePlayerSessionService(
            new GameSession(
                SessionId: "s",
                CharacterName: "Emraell",
                LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: Laeth));
        var chat = new FakeChatSessionService(
            new ChatSession(
                Server: "Arisetsu",
                Character: "Emraell",
                At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero));
        var composer = new SessionAgreementComposer(player, chat);
        var changedCount = 0;
        composer.Changed += (_, _) => changedCount++;

        try
        {
            composer.Start();

            changedCount.Should().Be(1);
            composer.ModuleId.Should().Be(SessionAgreementComposer.AttentionSourceId);
            composer.Count.Should().Be(1);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Matching_servers_for_same_character_emit_no_warning_and_no_attention()
    {
        var player = new FakePlayerSessionService(
            new GameSession(
                SessionId: "s",
                CharacterName: "Emraell",
                LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: Laeth));
        var chat = new FakeChatSessionService(
            new ChatSession(
                Server: "Laeth",
                Character: "Emraell",
                At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero));
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();

            diag.Entries.Should().BeEmpty();
            composer.Count.Should().Be(0);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Chat_only_observation_is_silent()
    {
        // Cold-start mid-PG-session: chat banner observed, Player.log session
        // never landed (preamble skipped).
        var player = new FakePlayerSessionService(current: null);
        var chat = new FakeChatSessionService(
            new ChatSession(
                Server: "Laeth",
                Character: "Emraell",
                At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero));
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();

            diag.Entries.Should().BeEmpty();
            composer.Count.Should().Be(0);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Player_only_observation_is_silent()
    {
        var player = new FakePlayerSessionService(
            new GameSession(
                SessionId: "s",
                CharacterName: "Emraell",
                LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: Laeth));
        var chat = new FakeChatSessionService(current: null);
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();

            diag.Entries.Should().BeEmpty();
            composer.Count.Should().Be(0);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Both_null_is_silent()
    {
        var player = new FakePlayerSessionService(current: null);
        var chat = new FakeChatSessionService(current: null);
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();

            diag.Entries.Should().BeEmpty();
            composer.Count.Should().Be(0);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Null_player_server_is_silent_even_when_chat_carries_one()
    {
        // Cold-start mid-PG-session: banner landed in replay but the connect
        // preamble didn't, so GameSession.Server is null.
        var player = new FakePlayerSessionService(
            new GameSession(
                SessionId: "s",
                CharacterName: "Emraell",
                LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: null));
        var chat = new FakeChatSessionService(
            new ChatSession(
                Server: "Laeth",
                Character: "Emraell",
                At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero));
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();

            diag.Entries.Should().BeEmpty();
            composer.Count.Should().Be(0);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Character_mismatch_does_not_trigger_warn_even_with_disagreeing_servers()
    {
        // Mid-character-swap: player session ahead of chat banner. The two
        // observations belong to different logical pairings; warning here
        // would be spurious.
        var player = new FakePlayerSessionService(
            new GameSession(
                SessionId: "s",
                CharacterName: "Emraell",
                LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: Laeth));
        var chat = new FakeChatSessionService(
            new ChatSession(
                Server: "Arisetsu",
                Character: "Frodo",
                At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero));
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();

            diag.Entries.Should().BeEmpty();
            composer.Count.Should().Be(0);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Same_logical_pairing_emits_only_once_when_player_session_re_published()
    {
        var player = new MutablePlayerSessionService();
        var chat = new MutableChatSessionService();
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();

            var initial = new GameSession(
                SessionId: "session-1",
                CharacterName: "Emraell",
                LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: Laeth);
            player.Publish(initial);
            chat.Publish(new ChatSession(
                Server: "Arisetsu",
                Character: "Emraell",
                At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero));

            diag.WarnCount(SessionAgreementComposer.DiagnosticCategory).Should().Be(1);

            // Re-publishing the same player session re-runs the check but the
            // (SessionId, ChatBannerAt) dedup keeps the warning to one.
            player.Publish(initial);
            diag.WarnCount(SessionAgreementComposer.DiagnosticCategory).Should().Be(1);
            composer.Count.Should().Be(1);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void New_chat_banner_against_same_disagreeing_player_session_re_fires()
    {
        // PG re-logs into chat (or chat banner re-emits) while the Player.log
        // session is unchanged — the chat-banner-At differs, so dedup admits
        // a fresh warning.
        var player = new MutablePlayerSessionService();
        var chat = new MutableChatSessionService();
        var diag = new RecordingDiagnosticsSink();

        var composer = new SessionAgreementComposer(player, chat, diag);
        try
        {
            composer.Start();
            player.Publish(new GameSession(
                SessionId: "session-1",
                CharacterName: "Emraell",
                LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
                TimezoneOffset: TimeSpan.Zero,
                Server: Laeth));
            chat.Publish(new ChatSession(
                Server: "Arisetsu",
                Character: "Emraell",
                At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero));
            chat.Publish(new ChatSession(
                Server: "Arisetsu",
                Character: "Emraell",
                At: new DateTimeOffset(2026, 5, 22, 0, 1, 0, TimeSpan.Zero),
                Offset: TimeSpan.Zero));

            diag.WarnCount(SessionAgreementComposer.DiagnosticCategory).Should().Be(2);
            composer.Count.Should().Be(2);
        }
        finally { composer.Dispose(); }
    }

    [Fact]
    public void Composer_does_not_mutate_either_source_session_record()
    {
        // Acceptance bullet 3 — no mutation; the Player.log-derived
        // GameSession.Server stays authoritative.
        var playerSession = new GameSession(
            SessionId: "s",
            CharacterName: "Emraell",
            LoggedInUtc: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc),
            TimezoneOffset: TimeSpan.Zero,
            Server: Laeth);
        var chatSession = new ChatSession(
            Server: "Arisetsu",
            Character: "Emraell",
            At: new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero),
            Offset: TimeSpan.Zero);
        var player = new FakePlayerSessionService(playerSession);
        var chat = new FakeChatSessionService(chatSession);

        var composer = new SessionAgreementComposer(player, chat);
        try
        {
            composer.Start();
            player.Current.Should().BeSameAs(playerSession);
            player.Current!.Server.Should().BeSameAs(Laeth);
            chat.Current.Should().BeSameAs(chatSession);
            chat.Current!.Server.Should().Be("Arisetsu");
        }
        finally { composer.Dispose(); }
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class FakePlayerSessionService : IGameSessionService
    {
        public FakePlayerSessionService(GameSession? current)
        {
            Current = current;
        }

        public GameSession? Current { get; }

        public event EventHandler<GameSession>? SessionStarted { add { } remove { } }

        public IDisposable Subscribe(Action<GameSession> handler)
        {
            if (Current is not null) handler(Current);
            return new NoOp();
        }

        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeChatSessionService : IChatSessionService
    {
        public FakeChatSessionService(ChatSession? current)
        {
            Current = current;
        }

        public ChatSession? Current { get; }

        public IDisposable Subscribe(Action<ChatSession> handler)
        {
            // Matches the IChatSessionService contract — handlers fire on every
            // new banner observation; Current is NOT replayed on subscribe.
            return new NoOp();
        }

        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }

    private sealed class MutablePlayerSessionService : IGameSessionService
    {
        private readonly List<Action<GameSession>> _subs = new();
        public GameSession? Current { get; private set; }
        public event EventHandler<GameSession>? SessionStarted;

        public IDisposable Subscribe(Action<GameSession> handler)
        {
            if (Current is not null) handler(Current);
            _subs.Add(handler);
            return new NoOp();
        }

        public void Publish(GameSession session)
        {
            Current = session;
            SessionStarted?.Invoke(this, session);
            foreach (var s in _subs.ToArray()) s(session);
        }

        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }

    private sealed class MutableChatSessionService : IChatSessionService
    {
        private readonly List<Action<ChatSession>> _subs = new();
        public ChatSession? Current { get; private set; }

        public IDisposable Subscribe(Action<ChatSession> handler)
        {
            _subs.Add(handler);
            return new NoOp();
        }

        public void Publish(ChatSession session)
        {
            Current = session;
            foreach (var s in _subs.ToArray()) s(session);
        }

        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }

    private sealed class RecordingDiagnosticsSink : IDiagnosticsSink
    {
        private readonly List<DiagnosticEntry> _entries = new();
        public IReadOnlyList<DiagnosticEntry> Entries => _entries;
        public event EventHandler<DiagnosticEntry>? EntryAdded;

        public void Write(DiagnosticLevel level, string category, string message)
        {
            var entry = new DiagnosticEntry(DateTime.UtcNow, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public IReadOnlyList<DiagnosticEntry> Snapshot() => _entries.ToArray();

        public int WarnCount(string category) =>
            _entries.Count(e => e.Level == DiagnosticLevel.Warn && e.Category == category);
    }
}
