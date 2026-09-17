using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// The issue form a player files a share code through. The Collector only
/// builds the address; the Desktop opens it in the system browser. Form fields are prefilled by query parameters
/// named after their <c>id</c>, and an address the server finds too long is refused with <c>414 URI Too Long</c>,
/// so a code that would push it past <see cref="MaxUrlLength"/> is left out and the player pastes it instead.
/// The repository is the one <see cref="SharedCalibrationClient"/> downloads from.
/// </summary>
public static class SharedCalibrationIssueLink
{
    /// <summary>Issue form template in the public repository.</summary>
    public const string FormTemplate = "share-calibration.yml";

    /// <summary>Longest address handed out; comfortably under what the issue form accepts.</summary>
    public const int MaxUrlLength = 7500;

    /// <summary>The prefilled form address, and whether the code is part of it.</summary>
    /// <param name="region">Region the code is for.</param>
    /// <param name="gameBuild">Build the code is for.</param>
    /// <param name="code">The share code.</param>
    public static (string Url, bool CodeInUrl) For(Region region, string gameBuild, string code) =>
        For(region, gameBuild, code, MaxUrlLength);

    /// <summary>As <see cref="For(Region, string, string)"/>, with the length limit given.</summary>
    internal static (string Url, bool CodeInUrl) For(Region region, string gameBuild, string code, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuild);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var title = "[共享校准] " + EnumWire<Region>.Format(region) + " " + gameBuild;
        var form = "https://github.com/" + SharedCalibrationClient.Owner + "/" + SharedCalibrationClient.Repository +
                   "/issues/new?template=" + Uri.EscapeDataString(FormTemplate) + "&title=" + Uri.EscapeDataString(title);
        var prefilled = form + "&code=" + Uri.EscapeDataString(code);
        return prefilled.Length <= maxLength ? (prefilled, true) : (form, false);
    }
}
