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

    /// <summary>
    /// The text the address prefills into the form's <c>basis</c> field (plan §20.4). It is a text input, not a dropdown -
    /// GitHub prefills inputs from the query string but not dropdowns - and the repository's Action accepts exactly two
    /// texts, <c>BASIS_OPTION_CALIBRATED</c> and <c>BASIS_OPTION_VERIFIED</c> in <c>tools/shared-calibration/issue.py</c>;
    /// any other non-empty text is refused there as <c>BASIS_INVALID</c>. So these must stay byte for byte the same as
    /// those two constants.
    /// </summary>
    /// <param name="basis">What the code rests on.</param>
    public static string BasisOption(SharedShareBasis basis) => basis switch
    {
        SharedShareBasis.Calibrated => "本机校准得到，并逐条核对过校准时间线",
        SharedShareBasis.Verified => "其他玩家分享，已在我的软件里核实通过并正常记录",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "not a share basis"),
    };

    /// <summary>The prefilled form address, and whether the code is part of it.</summary>
    /// <param name="region">Region the code is for.</param>
    /// <param name="gameBuild">Build the code is for.</param>
    /// <param name="code">The share code.</param>
    /// <param name="basis">What the code rests on; its text is prefilled into the form's <c>basis</c> field.</param>
    public static (string Url, bool CodeInUrl) For(Region region, string gameBuild, string code, SharedShareBasis basis) =>
        For(region, gameBuild, code, basis, MaxUrlLength);

    /// <summary>
    /// As <see cref="For(Region, string, string, SharedShareBasis)"/>, with the length limit given. The code goes last
    /// and is the only part ever dropped: the title and the basis are short and fixed, so the form without the code
    /// always fits.
    /// </summary>
    internal static (string Url, bool CodeInUrl) For(Region region, string gameBuild, string code, SharedShareBasis basis, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuild);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var title = "[共享校准] " + EnumWire<Region>.Format(region) + " " + gameBuild;
        var form = "https://github.com/" + SharedCalibrationClient.Owner + "/" + SharedCalibrationClient.Repository +
                   "/issues/new?template=" + Uri.EscapeDataString(FormTemplate) + "&title=" + Uri.EscapeDataString(title) +
                   "&basis=" + Uri.EscapeDataString(BasisOption(basis));
        var prefilled = form + "&code=" + Uri.EscapeDataString(code);
        return prefilled.Length <= maxLength ? (prefilled, true) : (form, false);
    }
}
