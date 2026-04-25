namespace Mithril.Shell.Updates;

public interface IUpdateChecker
{
    Task CheckAsync(CancellationToken ct);
}
