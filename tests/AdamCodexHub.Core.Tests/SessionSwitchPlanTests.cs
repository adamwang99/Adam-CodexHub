using AdamCodexHub.Core.Domain;
using Xunit;

namespace AdamCodexHub.Core.Tests;

public sealed class SessionSwitchPlanTests
{
    [Fact]
    public void RequiresNewSession_WhenTargetSessionIsMissing()
    {
        var plan = new SessionSwitchPlan
        {
            SourceProviderId = "deepseek",
            TargetProviderId = "ttmapi",
            ProjectState = new ProjectState
            {
                ProjectPath = @"C:\work\project",
                Revision = 10,
                UpdatedAt = DateTimeOffset.UtcNow
            }
        };

        Assert.True(plan.RequiresNewSession);
    }

    [Fact]
    public void DoesNotRequireNewSession_WhenTargetSessionExists()
    {
        var plan = new SessionSwitchPlan
        {
            SourceProviderId = "ttmapi",
            TargetProviderId = "deepseek",
            ProjectState = new ProjectState
            {
                ProjectPath = @"C:\work\project",
                Revision = 15,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            ExistingTargetSession = new SessionBinding
            {
                Id = "session-1",
                ProjectPath = @"C:\work\project",
                ProviderId = "deepseek",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                LastUsedAt = DateTimeOffset.UtcNow.AddHours(-1),
                LastSeenProjectRevision = 12
            }
        };

        Assert.False(plan.RequiresNewSession);
    }
}
