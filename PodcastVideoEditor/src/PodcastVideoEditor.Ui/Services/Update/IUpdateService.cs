using System.Threading;
using System.Threading.Tasks;

namespace PodcastVideoEditor.Ui.Services.Update;

public interface IUpdateService
{
    bool ShouldCheckForUpdates();

    Task<UpdateCheckResult> CheckForUpdatesAsync(bool ignoreSchedule, CancellationToken cancellationToken = default);

    Task<UpdateInstallResult> DownloadAndInstallUpdateAsync(UpdateCheckResult update, CancellationToken cancellationToken = default);
}
