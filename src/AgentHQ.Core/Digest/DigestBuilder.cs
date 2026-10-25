using System.Text;
using AgentHQ.Core.Domain;

namespace AgentHQ.Core.Digest;

public sealed record DigestItem(string Kind, string Text);

/// <summary>
/// Deterministic L1 session condensation — no AI summarization. Order: user messages
/// verbatim → assistant text parts (tail-weighted) → state-changing tool calls as one-liners.
/// Reasoning, step-*, timeline, compaction and raw tool output are dropped.
/// </summary>
public static class DigestBuilder
{
    public const int DefaultBudget = 8000;

    /// <summary>
    /// Budget is a hard upper bound. Elision order: (1) oldest assistant text parts, keeping
    /// the last 20% verbatim; (2) tool activity, keeping the newest calls; (3) oversized user
    /// messages, keeping their head and tail (they are usually pasted logs/tool dumps).
    /// </summary>
    public static string Render(SessionInfo info, IReadOnlyList<SessionMessage> messages, int budget = DefaultBudget)
    {
        var users = new List<string>();
        var assistant = new List<string>();
        var tools = new List<string>();

        foreach (var msg in messages)
        {
            foreach (var part in msg.Parts)
            {
                if (part.Type == "text" && !string.IsNullOrWhiteSpace(part.Text))
                {
                    if (msg.Role == "user") users.Add(part.Text.Trim());
                    else assistant.Add(part.Text.Trim());
                }
                else if (part.Type == "tool" && !string.IsNullOrWhiteSpace(part.ToolName))
                {
                    tools.Add(string.IsNullOrWhiteSpace(part.ToolSummary)
                        ? part.ToolName
                        : $"{part.ToolName}: {Compact(part.ToolSummary)}");
                }
            }
        }

        var header = $"# Session {info.Id}\n{info.Title} — {info.RootPath}\n" +
                     $"status: {info.Status(DateTimeOffset.UtcNow)}, updated {info.TimeUpdated:u}\n";
        var available = Math.Max(200, budget - header.Length);

        // Step 1: assistant text — keep the last 20% verbatim.
        var keepAssistant = assistant.Count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(assistant.Count * 0.2));
        var droppedAssistant = assistant.Count - keepAssistant;
        var keptAssistant = assistant.Skip(droppedAssistant).ToList();

        string Render(string? marker)
        {
            var sb = new StringBuilder();
            if (marker is not null) sb.Append('\n').Append(marker).Append('\n');
            foreach (var u in users) sb.Append("\n## user\n").Append(u).Append('\n');
            foreach (var a in keptAssistant) sb.Append("\n## assistant\n").Append(a).Append('\n');
            if (tools.Count > 0)
            {
                sb.Append("\n## tool activity\n");
                foreach (var t in tools) sb.Append("- ").Append(t).Append('\n');
            }
            return sb.ToString();
        }

        var marker1 = droppedAssistant > 0 ? $"(… {droppedAssistant} earlier items elided …)" : null;
        var body = Render(marker1);
        if (body.Length <= available) return header + body;

        // Step 2: tool activity — keep the newest calls.
        var droppedTools = tools.Count;
        while (tools.Count > 0 && body.Length > available)
        {
            var excess = body.Length - available;
            var remove = Math.Max(1, Math.Min(tools.Count, excess / Math.Max(1, AvgToolLen(tools)) + 1));
            tools.RemoveRange(0, Math.Min(remove, tools.Count));
            body = Render(ElisionMarker(droppedAssistant, droppedTools - tools.Count, 0));
        }

        // Step 3: user messages too large for what remains — head+tail truncation per message.
        if (body.Length > available)
        {
            var perUser = Math.Max(120, available / Math.Max(1, users.Count) - 40);
            for (var i = 0; i < users.Count; i++)
                users[i] = Truncate(users[i], perUser);
            body = Render(ElisionMarker(droppedAssistant, droppedTools, 0));
        }

        // Last resort: hard cut at the budget.
        var full = header + body;
        return full.Length <= budget
            ? full
            : full[..Math.Max(0, budget - 24)] + "\n(… truncated …)";
    }

    private static int AvgToolLen(List<string> tools) =>
        tools.Count == 0 ? 1 : Math.Max(1, tools.Sum(t => t.Length + 3) / tools.Count);

    private static string ElisionMarker(int assistantDropped, int toolsDropped, int usersTruncated)
    {
        var parts = new List<string>();
        if (assistantDropped > 0) parts.Add($"{assistantDropped} earlier assistant messages elided");
        if (toolsDropped > 0) parts.Add($"{toolsDropped} earlier tool calls elided");
        if (usersTruncated > 0) parts.Add($"{usersTruncated} long messages truncated");
        return parts.Count == 0 ? "" : $"(… {string.Join("; ", parts)} …)";
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = max * 3 / 4;
        var tail = max - head - 30;
        return s[..head] + "\n(… elided …)\n" + s[^Math.Max(0, tail)..];
    }

    private static string Compact(string s)
    {
        var oneLine = s.Replace('\n', ' ').Trim();
        return oneLine.Length <= 120 ? oneLine : oneLine[..117] + "…";
    }
}
