namespace PodcastVideoEditor.Ui.Services.Update;

public sealed class UpdateInstallResult
{
    public bool IsSuccessful { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? InstallerPath { get; init; }

    public static UpdateInstallResult Success(string message, string installerPath) => new()
    {
        IsSuccessful = true,
        Message = message,
        InstallerPath = installerPath
    };

    public static UpdateInstallResult Failure(string message) => new()
    {
        IsSuccessful = false,
        Message = message
    };
}
