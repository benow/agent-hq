using AgentHQ.Core.Plans;
using Xunit;

namespace AgentHQ.Tests;

public class PlanStateMachineTests
{
    [Theory]
    [InlineData(PlanStatus.Submitted, "start", PlanStatus.Refining)]
    [InlineData(PlanStatus.Refining, "review", PlanStatus.InReview)]
    [InlineData(PlanStatus.InReview, "sendback", PlanStatus.Refining)]
    [InlineData(PlanStatus.InReview, "implement", PlanStatus.Implementing)]
    [InlineData(PlanStatus.Implementing, "implemented", PlanStatus.Implemented)]
    [InlineData(PlanStatus.Fixing, "implemented", PlanStatus.Implemented)]
    [InlineData(PlanStatus.Implemented, "verify", PlanStatus.Verifying)]
    [InlineData(PlanStatus.Verifying, "pass", PlanStatus.Verified)]
    [InlineData(PlanStatus.Verifying, "fail", PlanStatus.Fixing)]
    [InlineData(PlanStatus.Verified, "done", PlanStatus.Done)]
    [InlineData(PlanStatus.Deferred, "reactivate", PlanStatus.Refining)]
    [InlineData(PlanStatus.Submitted, "defer", PlanStatus.Deferred)]
    [InlineData(PlanStatus.Done, "defer", PlanStatus.Deferred)]
    [InlineData(PlanStatus.Verifying, "abandon", PlanStatus.Abandoned)]
    public void LegalCommands(PlanStatus from, string command, PlanStatus to)
    {
        Assert.True(PlanStateMachine.CanTransition(from, command));
        Assert.Equal(to, PlanStateMachine.Target(from, command));
    }

    [Theory]
    [InlineData(PlanStatus.Submitted, "review")]      // must start first
    [InlineData(PlanStatus.Submitted, "implement")]   // must review first
    [InlineData(PlanStatus.Implementing, "implemented_x")]
    [InlineData(PlanStatus.Implementing, "pass")]     // verdicts only from verifying
    [InlineData(PlanStatus.Verifying, "done")]        // must pass first
    [InlineData(PlanStatus.Verified, "verify")]
    [InlineData(PlanStatus.Done, "reactivate")]       // only deferred reactivates
    [InlineData(PlanStatus.Deferred, "start")]
    public void IllegalCommands(PlanStatus from, string command)
    {
        Assert.False(PlanStateMachine.CanTransition(from, command));
    }

    [Fact]
    public void VerifiedOnlyReachableFromVerifying()
    {
        foreach (var from in Enum.GetValues<PlanStatus>().Where(s => s != PlanStatus.Verifying))
            Assert.False(PlanStateMachine.CanTransition(from, "pass"));
    }

    [Fact]
    public void ShortCutEntryOnlyForVerify()
    {
        Assert.True(PlanStateMachine.IsShortCutEntry("verify", PlanStatus.Implementing));
        Assert.True(PlanStateMachine.IsShortCutEntry("verify", PlanStatus.Fixing));
        Assert.False(PlanStateMachine.IsShortCutEntry("verify", PlanStatus.Implemented));
        Assert.False(PlanStateMachine.IsShortCutEntry("pass", PlanStatus.Implementing));
    }

    [Fact]
    public void ManualValidateMatchesCommandRules()
    {
        Assert.True(PlanStateMachine.Validate(PlanStatus.Implemented, PlanStatus.Verifying).IsAllowed);
        Assert.False(PlanStateMachine.Validate(PlanStatus.Implementing, PlanStatus.Verified).IsAllowed);
        Assert.False(PlanStateMachine.Validate(PlanStatus.Done, PlanStatus.Done).IsAllowed);
    }

    [Fact]
    public void WorkCommands()
    {
        Assert.True(PlanStateMachine.IsWorkCommand("start"));
        Assert.True(PlanStateMachine.IsWorkCommand("implement"));
        Assert.False(PlanStateMachine.IsWorkCommand("review"));
    }
}

public class PlanCommandTests
{
    [Fact]
    public void ParsesFileArg()
    {
        var c = PlanCommand.TryParse("/plan start docs/plans/x.md");
        Assert.NotNull(c);
        Assert.Equal("start", c!.Subcommand);
        Assert.Equal("docs/plans/x.md", c.Argument);
        Assert.Null(c.ReasonTail);
    }

    [Fact]
    public void ParsesReasonTail()
    {
        var c = PlanCommand.TryParse("/plan fail docs/plans/x.md tests are failing in parser");
        Assert.Equal("fail", c!.Subcommand);
        Assert.Equal("docs/plans/x.md", c.Argument);
        Assert.Equal("tests are failing in parser", c.ReasonTail);
    }

    [Fact]
    public void IgnoresReasonForCommandsWithoutIt()
    {
        var c = PlanCommand.TryParse("/plan implemented my-plan extra words");
        Assert.Equal("my-plan", c!.Argument);
        Assert.Null(c.ReasonTail);
    }

    [Fact]
    public void ParsesColonSpelling()
    {
        var c = PlanCommand.TryParse("/plan:start docs/plans/x.md");
        Assert.NotNull(c);
        Assert.Equal("start", c!.Subcommand);
        Assert.Equal("docs/plans/x.md", c.Argument);
        Assert.Null(c.ReasonTail);
    }

    [Fact]
    public void ColonSpellingCarriesReasonTail()
    {
        var c = PlanCommand.TryParse("/plan:fail docs/plans/x.md verification criterion 2 fails");
        Assert.Equal("fail", c!.Subcommand);
        Assert.Equal("docs/plans/x.md", c.Argument);
        Assert.Equal("verification criterion 2 fails", c.ReasonTail);
    }

    [Fact]
    public void ColonSpellingIgnoresTitleTail()
    {
        var c = PlanCommand.TryParse("/plan:start my-plan A Longer Title Here");
        Assert.Equal("my-plan", c!.Argument);
        Assert.Null(c.ReasonTail);
    }

    [Theory]
    [InlineData("/plan:")]
    [InlineData("/plan:start")]
    [InlineData("/plan: start docs/plans/x.md")]
    public void RejectsMalformedColonCommands(string prompt) => Assert.Null(PlanCommand.TryParse(prompt));

    [Theory]
    [InlineData("hello there")]
    [InlineData("/plan")]
    [InlineData("")]
    public void RejectsNonCommands(string prompt) => Assert.Null(PlanCommand.TryParse(prompt));
}

public class PlanDocumentTests
{
    private const string Doc = """
        # My plan

        ## Goal
        Ship it.

        ## Steps
        - [x] one
        - [ ] two
          - [X] nested sub-step
        * [ ] star item
        - [ ] not a checkbox without space

        ```text
        - [ ] inside fence still counts per the line rule
        ```

        ## Verification Criteria
        - [ ] not counted (outside Steps)

        ## Documentation
        README.
        """;

    [Fact]
    public void CountsCheckboxesInStepsOnly()
    {
        var p = PlanDocument.Progress(Doc);
        Assert.Equal(2, p.Checked);   // [x] one, [X] nested
        Assert.Equal(6, p.Total);     // one,two,nested,star,fence, and...
    }

    [Fact]
    public void MissingStepsReportsZero()
    {
        var p = PlanDocument.Progress("# x\n\n## Goal\nstuff\n");
        Assert.Equal(new PlanProgress(0, 0), p);
    }

    [Fact]
    public void SoftValidationNamesMissingSections()
    {
        var missing = PlanDocument.MissingSections("## Steps\n- [ ] a\n");
        Assert.Contains("Verification Criteria", missing);
        Assert.Contains("Documentation", missing);
        Assert.DoesNotContain("Steps", missing);
    }

    [Fact]
    public void SectionEndsAtNextH2()
    {
        Assert.Equal("Ship it.", PlanDocument.Section(Doc, "Goal"));
        Assert.DoesNotContain("Verification", PlanDocument.Section(Doc, "Steps"));
    }
}

public class LessonsParserTests
{
    [Fact]
    public void ParsesEntries()
    {
        var md = """
            # Lessons

            ### L-1: Always run the tests locally
            Category: tests
            Source: my-plan
            Run `dotnet test` before claiming done.

            ### L-2: Beware pooled connections
            Category: concurrency
            Source: other-plan
            data_version is per-connection.
            """;
        var entries = LessonsParser.Parse(md);
        Assert.Equal(2, entries.Count);
        Assert.Equal("L-1", entries[0].EntryId);
        Assert.Equal("Always run the tests locally", entries[0].Title);
        Assert.Equal("tests", entries[0].Category);
        Assert.Equal("my-plan", entries[0].SourcePlanId);
        Assert.Contains("dotnet test", entries[0].Body);
        Assert.Equal("concurrency", entries[1].Category);
    }
}

public class PlanSlugTests
{
    [Fact]
    public void DerivesFromName()
    {
        Assert.Equal("fix-the-player-bug", PlanSlug.Derive("Fix the Player Bug!", _ => false));
        Assert.Equal("a-b-c", PlanSlug.Derive("  A  B   C  ", _ => false));
        Assert.Equal("plan", PlanSlug.Derive("***", _ => false));
    }

    [Fact]
    public void SuffixesOnCollision()
    {
        var taken = new HashSet<string> { "my-plan", "my-plan-2" };
        Assert.Equal("my-plan-3", PlanSlug.Derive("my plan", taken.Contains));
    }

    [Fact]
    public void RepositoryIdFromRootPath()
    {
        Assert.Equal("-home-andy-projects-agent-hq", PlanSlug.ForRootPath("/home/andy/Projects/agent-hq/"));
    }
}
