using System;
using System.IO;
using AdamCodexHub.Codex;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// The tray item "switch the model of the open session" has to find the right
/// thread id and queue into the newest rollout, so both parts are pinned here.
/// </summary>
public sealed class CodexSessionQueueTests
{
    [Theory]
    [InlineData("rollout-2026-09-10T14-55-23-01a08a50-73d1-7a53-a163-1fe6b92aa8d2.jsonl", "01a08a50-73d1-7a53-a163-1fe6b92aa8d2")]
    [InlineData("rollout-2026-08-11T09-00-00-0f1e2d3c-0000-0000-0000-000000000000.jsonl", "0f1e2d3c-0000-0000-0000-000000000000")]
    public void ParseThreadId_ReadsThreadFromRolloutName(string fileName, string expected)
        => Assert.Equal(expected, CodexSessionQueue.ParseThreadId(fileName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-rollout.jsonl")]
    [InlineData("rollout-2026-09-10T14-55-23-not-a-guid.jsonl")]
    public void ParseThreadId_ReturnsNullForAnythingElse(string? fileName)
        => Assert.Null(CodexSessionQueue.ParseThreadId(fileName));

    [Fact]
    public void FindNewestSession_PicksTheMostRecentlyWrittenRollout()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexhub-queue-test-" + Guid.NewGuid().ToString("N"));
        var older = Path.Combine(root, "2026", "09", "09");
        var newer = Path.Combine(root, "2026", "09", "10");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);

        var olderFile = Path.Combine(older, "rollout-2026-09-09T10-00-00-8d8f7e6d-1111-2222-3333-444444444444.jsonl");
        var newerFile = Path.Combine(newer, "rollout-2026-09-10T14-55-23-01a08a50-73d1-7a53-a163-1fe6b92aa8d2.jsonl");
        File.WriteAllText(olderFile, "{}");
        File.WriteAllText(newerFile, "{}");
        File.SetLastWriteTimeUtc(olderFile, DateTime.UtcNow.AddHours(-5));
        File.SetLastWriteTimeUtc(newerFile, DateTime.UtcNow);

        try
        {
            var queue = new CodexSessionQueue { SessionsRoot = root };
            var session = queue.FindNewestSession();

            Assert.NotNull(session);
            Assert.Equal("01a08a50-73d1-7a53-a163-1fe6b92aa8d2", session!.Value.ThreadId);
            Assert.Equal(newerFile, session.Value.RolloutPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindNewestSession_ReturnsNullWhenThereIsNoSessionFolder()
    {
        var queue = new CodexSessionQueue
        {
            SessionsRoot = Path.Combine(Path.GetTempPath(), "codexhub-missing-" + Guid.NewGuid().ToString("N"))
        };

        Assert.Null(queue.FindNewestSession());
    }
}
