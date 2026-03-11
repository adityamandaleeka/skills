using System.Text.Json;
using SkillValidator.Models;
using SkillValidator.Utilities;
using GitHub.Copilot.SDK;

namespace SkillValidator.Services;

/// <summary>
/// Two-phase frontmatter uniqueness analysis:
///   Phase 1 (LLM): Generate discriminative prompts that would be ambiguous between skill pairs
///   Phase 2 (Real SDK): Run those prompts with both skills available, check co-activation
/// </summary>
public static class FrontmatterUniquenessAnalyzer
{
    private const int MaxRetries = 2;
    private const int MaxPairsPerBatch = 10;
    private const int PromptsPerPair = 3;

    public static async Task<FrontmatterUniquenessResult> Analyze(
        IReadOnlyList<SkillInfo> skills, FrontmatterUniquenessOptions options)
    {
        if (skills.Count < 2)
            return new FrontmatterUniquenessResult([], []);

        // Build all pairs
        var pairs = new List<(SkillInfo A, SkillInfo B)>();
        for (int i = 0; i < skills.Count; i++)
            for (int j = i + 1; j < skills.Count; j++)
                pairs.Add((skills[i], skills[j]));

        // Phase 1: Generate discriminative prompts for all pairs via LLM
        var allScores = new List<UniquenessScore>();
        foreach (var batch in pairs.Chunk(MaxPairsPerBatch))
        {
            var batchScores = await GenerateDiscriminativePromptsWithRetry(batch, options);
            allScores.AddRange(batchScores);
        }

        // Phase 2: For high-overlap pairs (distinctness <= 2), run real co-activation tests
        if (options.RunCoActivation)
        {
            var highOverlapPairs = allScores
                .Where(s => s.Distinctness <= 2 && s.DiscriminativePrompts is { Count: > 0 })
                .ToList();

            if (highOverlapPairs.Count > 0)
            {
                var skillsByName = skills.ToDictionary(s => s.Name);
                var updatedScores = new List<UniquenessScore>();

                foreach (var score in allScores)
                {
                    if (score.Distinctness <= 2 && score.DiscriminativePrompts is { Count: > 0 }
                        && skillsByName.TryGetValue(score.SkillA, out var skillA)
                        && skillsByName.TryGetValue(score.SkillB, out var skillB))
                    {
                        var coResults = await TestCoActivation(
                            skillA, skillB, score.DiscriminativePrompts, options);
                        updatedScores.Add(score with { CoActivationResults = coResults });
                    }
                    else
                    {
                        updatedScores.Add(score);
                    }
                }

                allScores = updatedScores;
            }
        }

        // Generate warnings
        var warnings = new List<string>();
        foreach (var score in allScores)
        {
            if (score.CoActivationResults is { Count: > 0 })
            {
                int coActivated = score.CoActivationResults.Count(r => r.BothActivated);
                if (coActivated > 0)
                {
                    warnings.Add(
                        $"Confirmed co-activation: \"{score.SkillA}\" and \"{score.SkillB}\" " +
                        $"both activated on {coActivated}/{score.CoActivationResults.Count} test prompt(s) — {score.Reasoning}");
                }
            }
            else if (score.Distinctness <= 2)
            {
                warnings.Add(
                    $"High overlap risk: \"{score.SkillA}\" and \"{score.SkillB}\" " +
                    $"(distinctness: {score.Distinctness}/5) — {score.Reasoning}");
            }
        }

        return new FrontmatterUniquenessResult(allScores, warnings);
    }

    // --- Phase 1: Discriminative prompt generation ---

    private static async Task<List<UniquenessScore>> GenerateDiscriminativePromptsWithRetry(
        (SkillInfo A, SkillInfo B)[] batch, FrontmatterUniquenessOptions options)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                    Console.Error.WriteLine($"      🔄 Uniqueness analyzer retry {attempt}/{MaxRetries}");
                return await GenerateDiscriminativePrompts(batch, options);
            }
            catch (Exception error)
            {
                lastError = error;
                Console.Error.WriteLine($"      ⚠️  Uniqueness analyzer attempt {attempt + 1} failed: {error.Message[..Math.Min(200, error.Message.Length)]}");
            }
        }

        throw new InvalidOperationException(
            $"Uniqueness analyzer failed after {MaxRetries + 1} attempts: {lastError}");
    }

    private static async Task<List<UniquenessScore>> GenerateDiscriminativePrompts(
        (SkillInfo A, SkillInfo B)[] batch, FrontmatterUniquenessOptions options)
    {
        var client = await AgentRunner.GetSharedClient(options.Verbose);

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = options.Model,
            Streaming = true,
            WorkingDirectory = options.WorkDir,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = BuildSystemPrompt(),
            },
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = (_, _) => Task.FromResult(new PermissionRequestResult
            {
                Kind = "denied-by-rules",
            }),
        });

        var userPrompt = BuildUserPrompt(batch);

        using var cts = new CancellationTokenSource(options.Timeout);
        using var timer = new Timer(_ =>
        {
            Console.Error.WriteLine(
                $"      ⏰ Uniqueness analyzer timed out after {options.Timeout / 1000}s.");
        }, null, options.Timeout, Timeout.Infinite);
        var done = new TaskCompletionSource<string>();
        string responseContent = "";

        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    responseContent = msg.Data.Content ?? "";
                    break;
                case SessionIdleEvent:
                    done.TrySetResult(responseContent);
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(new InvalidOperationException(err.Data.Message ?? "Session error"));
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = userPrompt });
        var content = await done.Task.WaitAsync(cts.Token);

        if (!string.IsNullOrEmpty(content))
            return ParseResponse(content, batch);

        throw new InvalidOperationException("Uniqueness analyzer returned no content");
    }

    internal static string BuildSystemPrompt() =>
        $$"""
        You are a skill uniqueness analyzer. You evaluate pairs of agent skill descriptions
        to determine how distinguishable they are from a routing perspective.

        When a user sends a prompt, a skill router sees ONLY the skill's name and description
        to decide which skills to load. If two skills have overlapping descriptions, both may
        load when only one is needed — wasting context window space and confusing the agent.

        For each pair of skills:
        1. Score their distinctness on a 1-5 scale:
           1 = Nearly identical — would almost always both trigger on the same prompts
           2 = High overlap — many prompts would trigger both, only subtle differences
           3 = Moderate overlap — some prompts could trigger both, but descriptions have clear differences
           4 = Low overlap — mostly distinct domains, only edge cases would trigger both
           5 = Completely distinct — no reasonable prompt would trigger both

        2. For pairs scoring 1-2, generate exactly {{PromptsPerPair}} discriminative prompts:
           user prompts that would plausibly trigger BOTH skills simultaneously.
           These must be:
           - Short and natural — the kind of thing a developer would actually type in a chat (1 sentence, under 15 words)
           - About a SINGLE concrete problem, not combining multiple concepts
           - NOT adversarial — don't jam keywords from both descriptions together
           Good: "my build is slow on re-runs"
           Bad: "diagnose my slow incremental MSBuild build using binlog timeline analysis"

        3. For pairs scoring 1-2, suggest a concrete fix for the descriptions.

        Respond with a JSON object containing a "pairs" array. Each element has:
        - "pair_index": the 0-based index of the pair
        - "distinctness": integer 1-5
        - "reasoning": brief explanation of overlap/distinction (1-3 sentences)
        - "suggested_fix": null if distinctness >= 3, otherwise a concrete suggestion
        - "discriminative_prompts": array of objects with "prompt" and "reasoning" fields
          (empty array if distinctness >= 3)

        Example response:
        ```json
        {
          "pairs": [
            {
              "pair_index": 0,
              "distinctness": 2,
              "reasoning": "Both skills address slow MSBuild builds with overlapping trigger phrases.",
              "suggested_fix": "Add 'Do not use for incremental build issues' to the baseline skill.",
              "discriminative_prompts": [
                {"prompt": "my build is slow on re-runs", "reasoning": "Ambiguous between baseline measurement and incremental build diagnosis"},
                {"prompt": "why does my project keep rebuilding", "reasoning": "Could be incremental build or general performance"},
                {"prompt": "help me speed up my dotnet build", "reasoning": "Generic performance request matching both skills"}
              ]
            }
          ]
        }
        ```
        """;

    internal static string BuildUserPrompt((SkillInfo A, SkillInfo B)[] batch)
    {
        var pairDescriptions = batch.Select((pair, i) =>
            $"""
            --- Pair {i} ---
            Skill A — Name: {pair.A.Name}
            Skill A — Description: {pair.A.Description}

            Skill B — Name: {pair.B.Name}
            Skill B — Description: {pair.B.Description}
            """).ToList();

        return $"""
            Analyze the following {batch.Length} pair(s) of skill descriptions for uniqueness.

            {string.Join("\n\n", pairDescriptions)}
            """;
    }

    internal static List<UniquenessScore> ParseResponse(
        string content, (SkillInfo A, SkillInfo B)[] batch)
    {
        var jsonStr = LlmJson.ExtractJson(content)
            ?? throw new InvalidOperationException(
                $"Uniqueness analyzer response contained no JSON. Raw response:\n{content[..Math.Min(500, content.Length)]}");

        var parsed = LlmJson.ParseLlmJson(jsonStr, "uniqueness analyzer response");

        var scores = new List<UniquenessScore>();

        if (parsed.TryGetProperty("pairs", out var pairsEl) && pairsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pairsEl.EnumerateArray())
            {
                var pairIndex = item.TryGetProperty("pair_index", out var idxEl) ? idxEl.GetInt32() : -1;
                var distinctness = item.TryGetProperty("distinctness", out var dEl) ? dEl.GetInt32() : 3;
                var reasoning = item.TryGetProperty("reasoning", out var rEl) ? rEl.GetString() ?? "" : "";
                var suggestedFix = item.TryGetProperty("suggested_fix", out var fEl) && fEl.ValueKind != JsonValueKind.Null
                    ? fEl.GetString() : null;

                if (pairIndex < 0 || pairIndex >= batch.Length) continue;

                // Parse discriminative prompts
                var prompts = new List<DiscriminativePrompt>();
                if (item.TryGetProperty("discriminative_prompts", out var promptsEl) && promptsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in promptsEl.EnumerateArray())
                    {
                        var prompt = p.TryGetProperty("prompt", out var pEl) ? pEl.GetString() ?? "" : "";
                        var pReasoning = p.TryGetProperty("reasoning", out var prEl) ? prEl.GetString() ?? "" : "";
                        if (prompt.Length > 0)
                            prompts.Add(new DiscriminativePrompt(prompt, pReasoning));
                    }
                }

                var (skillA, skillB) = batch[pairIndex];
                scores.Add(new UniquenessScore(
                    skillA.Name, skillB.Name,
                    skillA.Description, skillB.Description,
                    Math.Clamp(distinctness, 1, 5),
                    reasoning, suggestedFix,
                    prompts));
            }
        }

        return scores;
    }

    // --- Phase 2: Real co-activation testing ---

    private static async Task<List<CoActivationResult>> TestCoActivation(
        SkillInfo skillA, SkillInfo skillB,
        IReadOnlyList<DiscriminativePrompt> prompts,
        FrontmatterUniquenessOptions options)
    {
        var results = new List<CoActivationResult>();

        foreach (var dp in prompts)
        {
            try
            {
                var scenario = new EvalScenario(
                    Name: $"co-activation-test",
                    Prompt: dp.Prompt,
                    Timeout: 60);

                // Run with skill A only
                var metricsA = await AgentRunner.RunAgent(new RunOptions(
                    scenario, skillA, null, options.Model, options.Verbose));
                var activationA = MetricsCollector.ExtractSkillActivation(
                    metricsA.Events, new Dictionary<string, int>());

                // Run with skill B only
                var metricsB = await AgentRunner.RunAgent(new RunOptions(
                    scenario, skillB, null, options.Model, options.Verbose));
                var activationB = MetricsCollector.ExtractSkillActivation(
                    metricsB.Events, new Dictionary<string, int>());

                results.Add(new CoActivationResult(
                    dp.Prompt,
                    skillA.Name,
                    skillB.Name,
                    activationA.Activated,
                    activationB.Activated,
                    activationA.Activated && activationB.Activated));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"      ⚠️  Co-activation test failed for \"{dp.Prompt[..Math.Min(50, dp.Prompt.Length)]}\": {ex.Message[..Math.Min(100, ex.Message.Length)]}");
            }
        }

        return results;
    }
}

public sealed record FrontmatterUniquenessOptions(
    string Model,
    bool Verbose,
    int Timeout,
    string WorkDir,
    bool RunCoActivation = false);
