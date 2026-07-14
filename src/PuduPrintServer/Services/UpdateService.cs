using Velopack;
using Velopack.Sources;

namespace PuduPrintServer.Services;

public class UpdateService
{
    private static UpdateService? _i;
    public static UpdateService Instance => _i ??= new UpdateService();

    private const string RepoUrl = "https://github.com/hildenX/pudu-print-server";

    private UpdateManager? _mgr;
    private UpdateInfo?    _updateListo;

    public string? VersionLista => _updateListo?.TargetFullRelease.Version.ToString();
    public event Action<string>? UpdateReady;

    private UpdateService() { }

    public async Task CheckAndDownloadAsync()
    {
        try
        {
            _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (!_mgr.IsInstalled) return;
            var nueva = await _mgr.CheckForUpdatesAsync();
            if (nueva == null) return;
            await _mgr.DownloadUpdatesAsync(nueva);
            _updateListo = nueva;
            UpdateReady?.Invoke(nueva.TargetFullRelease.Version.ToString());
        }
        catch { /* sin red o sin release — silencioso */ }
    }

    public void ApplyAndRestart()
    {
        if (_mgr != null && _updateListo != null)
            _mgr.ApplyUpdatesAndRestart(_updateListo);
    }
}
