using System.CommandLine;
using SkillValidator.Models;
using SkillValidator.Services;

namespace SkillValidator.Commands;

public static class UniquenessCommand
{
    public static Command Create()
    {
        var pathsArg = new Argument<string[]>("paths") { Description = "Paths to skill directories or parent directories" };
        var verboseOpt = new Option<bool>("--verbose") { Description = "Show per-pair details for overlapping skills" };
        var modelOpt = new Option<string>("--model") { Description = "Model to use for analysis and co-activation tests", DefaultValueFactory = _ => "claude-opus-4.6" };
        var testsDirOpt = new Option<string?>("--tests-dir") { Description = "Directory containing test subdirectories" };
        var timeoutOpt = new Option<int>("--timeout") { Description = "LLM/agent timeout in seconds", DefaultValueFactory = _ => 300 };

        var command = new Command("uniqueness", "Analyze frontmatter descriptions for routing overlap and co-activation")
        {
            pathsArg,
            verboseOpt,
            modelOpt,
            testsDirOpt,
            timeoutOpt,
        };

        command.SetAction(async (parseResult, _) =>
        {
            var paths = parseResult.GetValue(pathsArg) ?? [];
            var verbose = parseResult.GetValue(verboseOpt);
            var model = parseResult.GetValue(modelOpt) ?? "claude-opus-4.6";
            var testsDir = parseResult.GetValue(testsDirOpt);
            var timeout = parseResult.GetValue(timeoutOpt) * 1000;

            return await Run(paths, model, verbose, testsDir, timeout);
        });

        return command;
    }

    private static async Task<int> Run(string[] paths, string model, bool verbose, string? testsDir, int timeout)
    {
        // Discover skills
        var allSkills = new List<SkillInfo>();
        foreach (var path in paths)
        {
            var skills = await SkillDiscovery.DiscoverSkills(path, testsDir);
            allSkills.AddRange(skills);
        }

        if (allSkills.Count == 0)
        {
            Console.Error.WriteLine("No skills found in the specified paths.");
            return 1;
        }

        Console.WriteLine($"Found {allSkills.Count} skill(s)\n");

        // Static profile analysis
        foreach (var skill in allSkills)
        {
            var profile = SkillProfiler.AnalyzeSkill(skill);
            Console.WriteLine(SkillProfiler.FormatProfileLine(profile));
            foreach (var warning in SkillProfiler.FormatProfileWarnings(profile))
                Console.WriteLine(warning);
        }

        // Uniqueness analysis
        if (allSkills.Count < 2)
        {
            Console.WriteLine("\nOnly 1 skill found — uniqueness analysis requires at least 2.");
            return 0;
        }

        Console.WriteLine("\n🔍 Analyzing frontmatter uniqueness across skills...");
        try
        {
            var result = await FrontmatterUniquenessAnalyzer.Analyze(
                allSkills,
                new FrontmatterUniquenessOptions(model, verbose, timeout,
                    Path.GetTempPath(), RunCoActivation: true));

            ReportUniqueness(result, verbose);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\x1b[33m⚠️  Frontmatter uniqueness analysis failed: {ex.Message}\x1b[0m");
            return 1;
        }

        await AgentRunner.StopSharedClient();
        await AgentRunner.CleanupWorkDirs();
        return 0;
    }

    internal static void ReportUniqueness(FrontmatterUniquenessResult result, bool verbose)
    {
        if (result.Warnings.Count > 0)
        {
            Console.WriteLine($"\x1b[33m⚠  Frontmatter uniqueness issues ({result.Warnings.Count}):\x1b[0m");
            foreach (var warning in result.Warnings)
                Console.WriteLine($"   ⚠  {warning}");
        }
        else
        {
            Console.WriteLine("✅ All skill descriptions are sufficiently distinct.");
        }

        var noteworthy = result.PairScores.Where(s => s.Distinctness <= 3).ToList();
        if (verbose && noteworthy.Count > 0)
        {
            Console.WriteLine();
            foreach (var score in noteworthy)
            {
                var icon = score.Distinctness <= 2 ? "\x1b[31m⚠\x1b[0m" : "\x1b[33m~\x1b[0m";
                Console.WriteLine($"   {icon} {score.SkillA} ↔ {score.SkillB}: {score.Distinctness}/5 — {score.Reasoning}");
                if (score.CoActivationResults is { Count: > 0 })
                {
                    foreach (var co in score.CoActivationResults)
                    {
                        var coIcon = co.BothActivated ? "\x1b[31m⚠\x1b[0m" : "\x1b[32m✓\x1b[0m";
                        Console.WriteLine($"     {coIcon} \"{co.Prompt}\" → A:{(co.SkillAActivated ? "✓" : "✗")} B:{(co.SkillBActivated ? "✓" : "✗")}");
                    }
                }
            }
        }

        int distinct = result.PairScores.Count(s => s.Distinctness >= 4);
        int total = result.PairScores.Count;
        Console.WriteLine($"\n   {distinct}/{total} pairs are clearly distinct (4-5/5), {noteworthy.Count} need attention (1-3/5)");
    }
}
