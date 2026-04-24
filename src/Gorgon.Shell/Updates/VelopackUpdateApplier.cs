using System.Diagnostics;
using Gorgon.Shared.Diagnostics;

namespace Gorgon.Shell.Updates;

public sealed class VelopackUpdateApplier : IUpdateApplier
{
    private readonly GorgonUpdateManager _holder;
    private readonly IDiagnosticsSink _diag;
    private int _applying;

    public VelopackUpdateApplier(GorgonUpdateManager holder, IDiagnosticsSink diag)
    {
        _holder = holder;
        _diag = diag;
    }

    public bool IsApplying => Volatile.Read(ref _applying) == 1;

    public async Task DownloadAndApplyAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _applying, 1) == 1) return;
        try
        {
            if (!_holder.IsAvailable || _holder.Pending is null)
            {
                _diag.Info("updates", "Apply requested but no pending update; opening releases page.");
                OpenReleasesPage();
                return;
            }

            // Portable extracts cannot self-update in place — there is no Update.exe co-located
            // and the install folder is wherever the user dropped the ZIP. Steer them to the
            // browser instead of failing inside Velopack.
            if (!_holder.IsInstalled)
            {
                _diag.Info("updates", "Portable build — opening releases page instead of applying in place.");
                OpenReleasesPage();
                return;
            }

            _diag.Info("updates", $"Downloading update {_holder.Pending.TargetFullRelease.Version} (channel={_holder.Channel.Name}).");
            await _holder.Manager.DownloadUpdatesAsync(_holder.Pending, cancelToken: ct).ConfigureAwait(false);

            _diag.Info("updates", "Restarting onto new version.");
            // Calls Update.exe and terminates this process. WPF teardown does not happen —
            // settings persisted via the Closing handler in Program.cs Main() will not run.
            // The trade-off: the in-flight save in Program.cs's finally block still fires
            // because Velopack spawns Update.exe and exits the current process via stdin signal,
            // letting Application.Run unblock and Program.Main's finally clause execute.
            _holder.Manager.ApplyUpdatesAndRestart(_holder.Pending);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _diag.Warn("updates", $"Apply failed: {ex.GetType().Name} {ex.Message}. Falling back to releases page.");
            OpenReleasesPage();
        }
        finally
        {
            Interlocked.Exchange(ref _applying, 0);
        }
    }

    private static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"{GorgonUpdateManager.RepoUrl}/releases/latest")
            {
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }
}
