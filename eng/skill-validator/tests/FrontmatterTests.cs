using SkillValidator.Models;
using SkillValidator.Services;

namespace SkillValidator.Tests;

public class FrontmatterDescriptionHeuristicTests
{
    private static SkillInfo MakeSkill(string description, string content = "---\nname: test\n---\n# Title\n1. Step\n```bash\necho\n```\n")
    {
        // Pad content to avoid unrelated warnings (detailed tier, has code blocks, steps, frontmatter)
        var padded = content + new string('x', 4000);
        return new SkillInfo("test-skill", description, "/tmp/test-skill",
            "/tmp/test-skill/SKILL.md", padded, null, null);
    }

    [Fact]
    public void WarnsOnShortDescription()
    {
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill("Fix builds"));
        Assert.Contains(profile.Warnings, w => w.Contains("too vague"));
        Assert.Equal(10, profile.DescriptionLength);
    }

    [Fact]
    public void WarnsOnLongDescription()
    {
        var longDesc = string.Concat(Enumerable.Repeat("Build performance optimization guide. ", 15));
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill(longDesc));
        Assert.Contains(profile.Warnings, w => w.Contains("trimming"));
        Assert.True(profile.DescriptionLength > 500);
    }

    [Fact]
    public void DetectsNegativeScope()
    {
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill(
            "Diagnose MSBuild failures using binary logs. Do not use for runtime errors or COM interop."));
        Assert.True(profile.HasNegativeScope);
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("negative scope"));
    }

    [Fact]
    public void WarnsOnMissingNegativeScope()
    {
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill(
            "Diagnose MSBuild failures using binary logs and error analysis tools."));
        Assert.False(profile.HasNegativeScope);
        Assert.Contains(profile.Warnings, w => w.Contains("negative scope"));
    }

    [Fact]
    public void DetectsActionVerbs()
    {
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill(
            "Diagnose and fix MSBuild build failures. Do not use for runtime issues."));
        Assert.True(profile.HasActionVerbs);
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("action verbs"));
    }

    [Fact]
    public void WarnsOnMissingActionVerbs()
    {
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill(
            "Reference material for compilation speed and caching strategies. Not for runtime."));
        Assert.False(profile.HasActionVerbs);
        Assert.Contains(profile.Warnings, w => w.Contains("action verbs"));
    }

    [Fact]
    public void DetectsSpecificitySignals()
    {
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill(
            "Diagnose error MSB4019 and NETSDK1045 in MSBuild projects. Do not use for runtime."));
        Assert.True(profile.HasSpecificitySignals);
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("specificity"));
    }

    [Fact]
    public void WarnsOnMissingSpecificitySignals()
    {
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill(
            "Optimize build speed and reduce compilation time. Do not use for testing."));
        Assert.False(profile.HasSpecificitySignals);
        Assert.Contains(profile.Warnings, w => w.Contains("specificity"));
    }

    [Fact]
    public void WellScopedDescriptionProducesNoDescriptionWarnings()
    {
        var profile = SkillProfiler.AnalyzeSkill(MakeSkill(
            "Diagnose and fix MSBuild build failures using binary logs and error MSB4019. Do not use for runtime errors."));
        // Should have no description-related warnings (may have structural warnings depending on content)
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("too vague"));
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("trimming"));
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("action verbs"));
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("negative scope"));
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("specificity"));
    }

    [Fact]
    public void SkipsDescriptionChecksWhenNoFrontmatter()
    {
        var skill = new SkillInfo("test", "", "/tmp/test",
            "/tmp/test/SKILL.md", "# Just content\n1. Step\n```bash\necho\n```\n" + new string('x', 4000),
            null, null);
        var profile = SkillProfiler.AnalyzeSkill(skill);
        // Should warn about missing frontmatter but NOT about description content
        Assert.Contains(profile.Warnings, w => w.Contains("frontmatter"));
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("action verbs"));
        Assert.DoesNotContain(profile.Warnings, w => w.Contains("negative scope"));
    }
}

public class FrontmatterUniquenessParseTests
{
    [Fact]
    public void ParsesValidResponse()
    {
        var batch = new[]
        {
            (new SkillInfo("skill-a", "Desc A", "/a", "/a/SKILL.md", "", null, null),
             new SkillInfo("skill-b", "Desc B", "/b", "/b/SKILL.md", "", null, null)),
        };

        var json = """
            {
              "pairs": [
                {
                  "pair_index": 0,
                  "distinctness": 2,
                  "reasoning": "Both deal with build performance",
                  "suggested_fix": "Add specific trigger signals"
                }
              ]
            }
            """;

        var scores = FrontmatterUniquenessAnalyzer.ParseResponse(json, batch);

        Assert.Single(scores);
        Assert.Equal("skill-a", scores[0].SkillA);
        Assert.Equal("skill-b", scores[0].SkillB);
        Assert.Equal(2, scores[0].Distinctness);
        Assert.NotNull(scores[0].SuggestedFix);
    }

    [Fact]
    public void ClampsDistinctnessToValidRange()
    {
        var batch = new[]
        {
            (new SkillInfo("a", "A", "/a", "/a/SKILL.md", "", null, null),
             new SkillInfo("b", "B", "/b", "/b/SKILL.md", "", null, null)),
        };

        var json = """
            {
              "pairs": [
                {"pair_index": 0, "distinctness": 7, "reasoning": "Very different", "suggested_fix": null}
              ]
            }
            """;

        var scores = FrontmatterUniquenessAnalyzer.ParseResponse(json, batch);
        Assert.Equal(5, scores[0].Distinctness);
    }

    [Fact]
    public void HandlesMissingPairsGracefully()
    {
        var batch = new[]
        {
            (new SkillInfo("a", "A", "/a", "/a/SKILL.md", "", null, null),
             new SkillInfo("b", "B", "/b", "/b/SKILL.md", "", null, null)),
        };

        var json = """{"pairs": []}""";

        var scores = FrontmatterUniquenessAnalyzer.ParseResponse(json, batch);
        Assert.Empty(scores);
    }

    [Fact]
    public void ParsesDiscriminativePrompts()
    {
        var batch = new[]
        {
            (new SkillInfo("perf-a", "Slow builds", "/a", "/a/SKILL.md", "", null, null),
             new SkillInfo("perf-b", "Also slow builds", "/b", "/b/SKILL.md", "", null, null)),
        };

        var json = """
            {
              "pairs": [
                {
                  "pair_index": 0,
                  "distinctness": 2,
                  "reasoning": "Both target slow builds",
                  "suggested_fix": "Add exclusions",
                  "discriminative_prompts": [
                    {"prompt": "my build is slow", "reasoning": "Generic slow build prompt"},
                    {"prompt": "help me speed up compilation", "reasoning": "Ambiguous performance request"}
                  ]
                }
              ]
            }
            """;

        var scores = FrontmatterUniquenessAnalyzer.ParseResponse(json, batch);
        Assert.Single(scores);
        Assert.NotNull(scores[0].DiscriminativePrompts);
        Assert.Equal(2, scores[0].DiscriminativePrompts!.Count);
        Assert.Equal("my build is slow", scores[0].DiscriminativePrompts![0].Prompt);
    }

    [Fact]
    public void OmitsDiscriminativePromptsForDistinctPairs()
    {
        var batch = new[]
        {
            (new SkillInfo("a", "A", "/a", "/a/SKILL.md", "", null, null),
             new SkillInfo("b", "B", "/b", "/b/SKILL.md", "", null, null)),
        };

        var json = """
            {
              "pairs": [
                {"pair_index": 0, "distinctness": 4, "reasoning": "Different domains", "suggested_fix": null, "discriminative_prompts": []}
              ]
            }
            """;

        var scores = FrontmatterUniquenessAnalyzer.ParseResponse(json, batch);
        Assert.Empty(scores[0].DiscriminativePrompts!);
    }
}
