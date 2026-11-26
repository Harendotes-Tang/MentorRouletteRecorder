using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Community;

namespace MentorRecorder.Collector.Ipc;

internal static class CommunityHandlers
{
    internal static async Task<JsonObject> HandleAsync(CollectorHost host, IpcRequest request, CancellationToken token)
    {
        try
        {
            var payload = request.Payload;
            var action = CommunityContent.String(payload, "action", 20);
            var result = action switch
            {
                "list" => host.CommunityWorkspace.List(Offset(payload)),
                "browse" => host.CommunityLibrary.Browse(Offset(payload)),
                "refresh" => await host.CommunityLibrary.RefreshAsync(token).ConfigureAwait(false),
                "online" => SetOnline(host, payload),
                "save" or "delete" or "fork" or "install" or "apply" or "import_file" =>
                    host.CommunityWorkspace.Change(action, request.RequestId, payload),
                "account" or "sign_in" or "poll" or "cancel" or "sign_out" or "submit" or "issue_status" =>
                    await GithubAsync(host, action, payload, token).ConfigureAwait(false),
                "prepare_issue" => new JsonObject
                {
                    ["status"] = "prepared",
                    ["body"] = CommunityGithub.BuildIssueBody(payload),
                },
                "copy" => Copy(payload),
                _ => CommunityWorkspace.Result("invalid_action"),
            };
            result["app_configured"] = host.CommunityGithub.Configured;
            return result;
        }
        catch (FormatException error)
        {
            return CommunityWorkspace.Result(error.Message);
        }
        catch (JsonException)
        {
            return CommunityWorkspace.Result("invalid_json");
        }
    }

    // The diagnostics report states the most recent community request of either kind; the
    // library keeps that record, so a GitHub request is handed to it once it has ended.
    private static async Task<JsonObject> GithubAsync(CollectorHost host, string action, JsonObject payload,
        CancellationToken token)
    {
        try
        {
            return await host.CommunityGithub.RequestAsync(action, payload, token).ConfigureAwait(false);
        }
        finally
        {
            host.CommunityLibrary.RecordRequest(host.CommunityGithub.LastRequest);
        }
    }

    private static int Offset(JsonObject payload)
    {
        var offset = payload["offset"] is null ? 0 : CommunityContent.Integer(payload, "offset");
        return offset is >= 0 and <= CommunityContent.MaxEntries ? offset : throw new FormatException("invalid_offset");
    }

    private static JsonObject SetOnline(CollectorHost host, JsonObject payload)
    {
        host.CommunityLibrary.SetOnline(CommunityContent.Boolean(payload, "enabled"));
        return host.CommunityLibrary.Browse();
    }

    private static JsonObject Copy(JsonObject payload)
    {
        var body = CommunityContent.String(payload, "body", 1600, multiline: true);
        var mode = CommunityContent.String(payload, "mode", 8);
        if (string.IsNullOrWhiteSpace(body))
            return CommunityWorkspace.Result("empty_body");
        if (mode is not ("reminder" or "chat" or "macro"))
            return CommunityWorkspace.Result("invalid_mode");
        if (mode != "reminder" && CommunityContent.MacroError(body, mode) is { } error)
            return CommunityWorkspace.Result(error);
        return new JsonObject { ["status"] = "copy_ready", ["body"] = body };
    }
}
