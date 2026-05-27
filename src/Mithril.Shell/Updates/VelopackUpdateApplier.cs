using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Mithril.Shared;

namespace Mithril.Shell.Updates;

public sealed class VelopackUpdateApplier : IUpdateApplier
{
    private readonly MithrilUpdateManager _holder;
    private readonly ILogger _logger;
    private int _applying;

    public VelopackUpdateApplier(MithrilUpdateManager holder, ILogger logger)
    {
        _holder = holder;
        _logger = logger;
    }

    public bool IsApplying => Volatile.Read(ref _applying) == 1;

    public async Task DownloadAndApplyAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _applying, 1) == 1) return;
        try
        {
            if (!_holder.IsAvailable || _holder.Pending is null)
            {
                _logger.LogInformation("Apply requested but no pending update; opening releases page.");
                OpenReleasesPage();
                return;
            }

            // Portable extracts cannot self-update in place — there is no Update.exe co-located
            // and the install folder is wherever the user dropped the ZIP. Steer them to the
            // browser instead of failing inside Velopack.
            if (!_holder.IsInstalled)
            {
                _logger.LogInformation("Portable build — opening releases page instead of applying in place.");
                OpenReleasesPage();
                return;
            }

            _logger.LogInformation($"Downloading update {_holder.Pending.TargetFullRelease.Version} (channel={_holder.Channel.Name}).");
            await _holder.Manager.DownloadUpdatesAsync(_holder.Pending, cancelToken: ct).ConfigureAwait(false);

            _logger.LogInformation("Restarting onto new version.");
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
            _logger.LogWarning(ex, "Apply failed: {ExceptionType} {Message}. Falling back to releases page.", ex.GetType().Name, ex.Message);
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
            Process.Start(new ProcessStartInfo($"{MithrilRepository.Url}/releases/latest")
            {
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }
}
