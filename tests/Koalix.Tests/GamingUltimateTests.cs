using System.Reflection;
using System.Security.Cryptography;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;

namespace Koalix.Tests;

/// <summary>v0.4.50 — the Ultimate Performance toggle that could never turn off («حالت ultra performance همیشه
/// روشنه و خاموشم نمیشه»). Real behavioral tests against a scripted powercfg: apply must pick the Ultimate-named
/// LINE of /list (not the first GUID of the whole listing), and revert must never re-activate an Ultimate scheme.</summary>
public class GamingUltimateTests
{
#pragma warning disable CA1416 // GamingOptimizer is windows-flavored; the fake runner never touches Windows here

    private sealed class FakeRunner : IProcessRunner
    {
        private string _active;
        public List<string> Calls { get; } = [];
        public string List { get; set; } = "";

        /// <param name="initial">the GUID /getactivescheme reports before any /setactive lands</param>
        public FakeRunner(string? initial = null) => _active = initial ?? "381b4226-f694-41f0-9685-ff5bb260df2e";

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, bool elevated = false, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var a = arguments.ToArray();
            Calls.Add(string.Join(' ', a));
            if (a.Length >= 2 && a[0] == "/setactive") _active = a[1];
            var out1 = (a.FirstOrDefault() ?? "") switch
            {
                "/list" => List,
                "/getactivescheme" => $"Power Scheme GUID: {_active}  (Active)",
                "/duplicatescheme" => "Power Scheme GUID: 77777777-7777-7777-7777-777777777777  (Ultimate Performance)",
                _ => "",
            };
            return Task.FromResult(new ProcessResult(0, out1, ""));
        }
    }

    private static GamingOptimizer NewOpt(FakeRunner runner, string tag) =>
        new(runner, new NetworkStateJournal(Path.Combine(Path.GetTempPath(), $"koalix-{tag}-{Guid.NewGuid():N}.json")));

    private static async Task<TweakResult> Invoke(GamingOptimizer opt, string method, params object?[] args)
    {
        var m = typeof(GamingOptimizer).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == method);
        return (TweakResult)(await (Task<TweakResult>)m.Invoke(opt, args)!);
    }

    private const string Balanced = "381b4226-f694-41f0-9685-ff5bb260df2e";

    [Fact]
    public async Task Ultimate_apply_matches_the_ultimate_LINE_not_the_whole_listing()
    {
        // the listing holds an Ultimate COPY with a random GUID — the old whole-output Contains("Ultimate")
        // matched the FIRST GUID of the entire listing (Balanced) and activated an arbitrary scheme
        var runner = new FakeRunner { List = """
            Power Scheme GUID: 381b4226-f694-41f0-9685-ff5bb260df2e  (Balanced)
            Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)
            Power Scheme GUID: 44444444-4444-4444-4444-444444444444  (Ultimate Performance)
            """ };
        var r = await Invoke(NewOpt(runner, "apply"), "ApplyUltimateAsync", GamingTweak.UltimatePower, CancellationToken.None);
        Assert.Contains("/setactive 44444444-4444-4444-4444-444444444444", runner.Calls);
        Assert.Equal(TweakOutcome.Applied, r.Outcome);
    }

    [Fact]
    public async Task Ultimate_apply_duplicates_when_no_copy_exists_yet()
    {
        var runner = new FakeRunner { List = "Power Scheme GUID: 381b4226-f694-41f0-9685-ff5bb260df2e  (Balanced)" };
        var r = await Invoke(NewOpt(runner, "dup"), "ApplyUltimateAsync", GamingTweak.UltimatePower, CancellationToken.None);
        Assert.Contains("/duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61", runner.Calls);
        Assert.Contains("/setactive 77777777-7777-7777-7777-777777777777", runner.Calls);
        Assert.Equal(TweakOutcome.Applied, r.Outcome);
    }

    [Fact]
    public async Task Ultimate_revert_never_restores_an_ultimate_scheme()
    {
        // the system currently SITS on the Ultimate copy and the journal's "before" is that copy itself
        var runner = new FakeRunner("55555555-5555-5555-5555-555555555555") { List = """
            Power Scheme GUID: 381b4226-f694-41f0-9685-ff5bb260df2e  (Balanced)
            Power Scheme GUID: 55555555-5555-5555-5555-555555555555  (Ultimate Performance)
            """ };
        var opt = NewOpt(runner, "rev");
        // a gamer who ran Ultimate pre-KOALIX: the old revert re-activated it, flipping the switch straight back ON («خاموشم نمیشه»)
        await Invoke(opt, "RevertCoreAsync", GamingTweak.UltimatePower, "55555555-5555-5555-5555-555555555555", CancellationToken.None);
        Assert.Contains($"/setactive {Balanced}", runner.Calls);

        // and the well-known Ultimate GUID as "before" must also fall back to Balanced
        var runner2 = new FakeRunner("e9a42b02-d5df-448d-aa00-03f14749eb61") { List = $"Power Scheme GUID: {Balanced}  (Balanced)" };
        await Invoke(NewOpt(runner2, "rev2"), "RevertCoreAsync", GamingTweak.UltimatePower, "e9a42b02-d5df-448d-aa00-03f14749eb61", CancellationToken.None);
        Assert.Contains($"/setactive {Balanced}", runner2.Calls);
    }

    [Fact]
    public async Task Ultimate_revert_restores_a_non_ultimate_scheme_faithfully()
    {
        // active = Balanced, "before" = a plain custom plan: restore must pick the ORIGINAL plan, not Balanced
        var runner = new FakeRunner() { List = "Power Scheme GUID: 66666666-6666-6666-6666-666666666666  (MyPowerPlan)" };
        await Invoke(NewOpt(runner, "rev3"), "RevertCoreAsync", GamingTweak.UltimatePower, "66666666-6666-6666-6666-666666666666", CancellationToken.None);
        Assert.Contains("/setactive 66666666-6666-6666-6666-666666666666", runner.Calls);
    }

#pragma warning restore CA1416
}
