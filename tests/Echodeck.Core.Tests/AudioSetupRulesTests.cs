using Echodeck.Core.Setup;

namespace Echodeck.Core.Tests;

public class AudioSetupRulesTests
{
    private const string Headset = "Headset Earphone (HyperX Cloud Flight S Chat)";
    private const string HeadsetMic = "Microphone (HyperX Cloud Flight S)";
    private const string CableIn = "CABLE Input (VB-Audio Virtual Cable)";
    private const string CableOut = "CABLE Output (VB-Audio Virtual Cable)";

    private static AudioSetupSnapshot Correct() => new(
        VirtualCableInstalled: true,
        WindowsDefaultOutput: Headset,
        EchodeckMicrophone: HeadsetMic,
        EchodeckDiscordOutput: CableIn,
        PreviewDevice: Headset,
        DiscordRunning: true,
        DiscordOutputDevice: Headset,
        DiscordInputDevice: CableOut,
        WholeDeviceCaptureDevice: null);

    [Fact]
    public void CorrectSetup_HasNoIssues() => Assert.Empty(AudioSetupRules.Evaluate(Correct()));

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", true)]
    [InlineData("Headset Earphone (HyperX Cloud Flight S Chat)", false)]
    [InlineData("Speakers (Steam Streaming Microphone)", false)]
    [InlineData(null, false)]
    public void RecognisesVirtualCable(string? name, bool expected) =>
        Assert.Equal(expected, AudioSetupRules.IsVirtualCable(name));

    [Fact]
    public void WindowsOutputOnCable_Warns() =>
        AssertSingleIssue(Correct() with { WindowsDefaultOutput = "CABLE In 16ch (VB-Audio Virtual Cable)" }, "Windows sound output");

    [Fact]
    public void CableMissing_IsError()
    {
        var issues = AudioSetupRules.Evaluate(Correct() with { VirtualCableInstalled = false, EchodeckDiscordOutput = null, DiscordInputDevice = null });
        var issue = Assert.Single(issues);
        Assert.Equal(SetupSeverity.Error, issue.Severity);
        Assert.Contains("VB-CABLE is not installed", issue.Problem);
    }

    [Fact]
    public void EchodeckOutputToHeadset_Warns() =>
        AssertSingleIssue(Correct() with { EchodeckDiscordOutput = Headset }, "you'll hear your own mic");

    [Fact]
    public void MicIsCable_IsError() =>
        AssertSingleIssue(Correct() with { EchodeckMicrophone = CableOut }, "loops Echodeck's output");

    [Fact]
    public void NoMic_Warns() =>
        AssertSingleIssue(Correct() with { EchodeckMicrophone = null }, "No microphone");

    [Fact]
    public void PreviewOnCable_Warns() =>
        AssertSingleIssue(Correct() with { PreviewDevice = CableIn }, "Local previews");

    [Fact]
    public void DiscordOutputOnCable_IsError() =>
        AssertSingleIssue(Correct() with { DiscordOutputDevice = CableIn }, "won't hear your friends");

    [Fact]
    public void DiscordInputIsRealMic_Warns() =>
        AssertSingleIssue(Correct() with { DiscordInputDevice = HeadsetMic }, "never your clips");

    [Fact]
    public void DiscordNotRunning_SkipsDiscordChecks() =>
        Assert.Empty(AudioSetupRules.Evaluate(Correct() with { DiscordRunning = false, DiscordOutputDevice = CableIn, DiscordInputDevice = HeadsetMic }));

    private static void AssertSingleIssue(AudioSetupSnapshot snapshot, string expectedText)
    {
        var issue = Assert.Single(AudioSetupRules.Evaluate(snapshot));
        Assert.Contains(expectedText, issue.Problem);
    }
}
