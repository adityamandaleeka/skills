#!/usr/bin/env node
/**
 * compare-summary.mjs — Generate a markdown comparison table from
 * vally baseline + skilled JSONL results.
 *
 * Usage: node compare-summary.mjs <eval-dir>
 *   eval-dir should contain baseline/, skilled/, and optionally results.json
 *
 * Outputs GitHub-flavored markdown to stdout.
 */
import { readFileSync, existsSync } from "fs";
import { join } from "path";
import { execSync } from "child_process";

const evalDir = process.argv[2];
if (!evalDir) {
  console.error("Usage: node compare-summary.mjs <eval-dir>");
  process.exit(1);
}

function readJsonl(path) {
  const lines = readFileSync(path, "utf-8").trim().split("\n").filter((l) => l.trim());
  let failures = 0;
  const results = [];
  for (const l of lines) {
    try { results.push(JSON.parse(l)); }
    catch { failures++; }
  }
  if (failures > 0) console.log(`⚠️ ${failures} malformed line(s) in ${path}`);
  if (results.length === 0) console.log(`⚠️ No valid results in ${path}`);
  return results;
}

function latestJsonl(dir) {
  try {
    return execSync(`find "${dir}" -name "*.jsonl" -type f 2>/dev/null | sort | tail -1`, {
      encoding: "utf-8",
    }).trim() || null;
  } catch { return null; }
}

// Read data
const baselineFile = latestJsonl(join(evalDir, "baseline"));
const skilledFile = latestJsonl(join(evalDir, "skilled"));
const resultsFile = join(evalDir, "results.json");

if (!baselineFile || !skilledFile) {
  console.log("⚠️ Missing baseline or skilled results");
  process.exit(0);
}

const baseline = readJsonl(baselineFile);
const skilled = readJsonl(skilledFile);

// Verdict from adapt
if (existsSync(resultsFile)) {
  try {
    const verdict = JSON.parse(readFileSync(resultsFile, "utf-8")).verdicts?.[0];
    if (verdict) {
      console.log(`**Verdict:** ${verdict.passed ? "✅" : "❌"} ${verdict.reason}`);
      console.log("");
    }
  } catch { /* skip */ }
}

// Group trials by stimulus name
const byStim = new Map();
for (const r of baseline) {
  const name = r.gradeResult?.stimulusName;
  if (!name || name === "unknown") continue;
  if (!byStim.has(name)) byStim.set(name, { baseline: [], skilled: [] });
  byStim.get(name).baseline.push(r);
}
for (const r of skilled) {
  const name = r.gradeResult?.stimulusName;
  if (!name || name === "unknown") continue;
  if (!byStim.has(name)) byStim.set(name, { baseline: [], skilled: [] });
  byStim.get(name).skilled.push(r);
}

// Pairwise scores from results.json
const pairwise = new Map();
if (existsSync(resultsFile)) {
  try {
    const verdict = JSON.parse(readFileSync(resultsFile, "utf-8")).verdicts?.[0];
    for (const s of (verdict?.scenarios || [])) {
      if (!pairwise.has(s.scenarioName)) pairwise.set(s.scenarioName, []);
      pairwise.get(s.scenarioName).push(s.improvementScore);
    }
  } catch { /* skip */ }
}

// Extract per-rubric scores from prompt grader
function getRubricScores(trials) {
  const allScores = new Map();
  for (const t of trials) {
    const prompt = (t.gradeResult?.details || []).find((d) => d.name === "prompt");
    for (const rs of (prompt?.metadata?.rubric_scores || [])) {
      if (!allScores.has(rs.criterion)) allScores.set(rs.criterion, []);
      allScores.get(rs.criterion).push(rs.score);
    }
  }
  const avg = new Map();
  for (const [c, scores] of allScores) {
    avg.set(c, scores.reduce((a, b) => a + b, 0) / scores.length);
  }
  return avg;
}

function graderPassRate(trials) {
  if (trials.length === 0) return "—";
  const passed = trials.filter((t) => t.gradeResult?.passed).length;
  return `${passed}/${trials.length}`;
}

// Render per-scenario tables
for (const [stimName, arms] of byStim) {
  const bScores = getRubricScores(arms.baseline);
  const sScores = getRubricScores(arms.skilled);
  const allCriteria = [...new Set([...bScores.keys(), ...sScores.keys()])];

  const pw = pairwise.get(stimName) || [];
  const avgPw = pw.length > 0 ? pw.reduce((a, b) => a + b, 0) / pw.length : null;

  console.log(`#### ${stimName}`);
  const parts = [`baseline ${graderPassRate(arms.baseline)}`, `skilled ${graderPassRate(arms.skilled)}`];
  if (avgPw !== null) parts.push(`judge: ${avgPw >= 0 ? "+" : ""}${(avgPw * 100).toFixed(1)}%`);
  console.log(`Graders: ${parts.join(" | ")}`);
  console.log("");

  if (allCriteria.length > 0) {
    console.log("| Rubric Criterion | Baseline | Skilled | Δ |");
    console.log("|-----------------|----------|---------|---|");
    for (const c of allCriteria) {
      const b = bScores.get(c);
      const s = sScores.get(c);
      const bStr = b != null ? `${b.toFixed(1)}/5` : "—";
      const sStr = s != null ? `${s.toFixed(1)}/5` : "—";
      let delta = "";
      if (b != null && s != null) {
        const d = s - b;
        if (d > 0.1) delta = `+${d.toFixed(1)} ↑`;
        else if (d < -0.1) delta = `${d.toFixed(1)} ↓`;
        else delta = "=";
      }
      // Escape pipes and newlines for markdown table cells
      const safeC = c.replace(/\|/g, "\\|").replace(/\n/g, " ");
      console.log(`| ${safeC} | ${bStr} | ${sStr} | ${delta} |`);
    }
    console.log("");
  } else {
    console.log("⚠️ No rubric scores available for this scenario");
    console.log("");
  }
}
