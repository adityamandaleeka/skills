#!/usr/bin/env bash
#
# run-vally-evals.sh — Run vally evaluations locally, mirroring the CI workflow.
#
# Usage:
#   ./eng/vally-adapter/run-vally-evals.sh                        # all plugins
#   ./eng/vally-adapter/run-vally-evals.sh dotnet-maui             # one plugin
#   ./eng/vally-adapter/run-vally-evals.sh dotnet-maui maui-theming  # one skill
#
# Prerequisites:
#   - ~/code/evaluate built (npm ci && npm run build)
#   - GITHUB_TOKEN set for Copilot SDK
#
# Results go to ./vally-results/<plugin>/<skill>/

set -euo pipefail

SKILLS_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
EVALUATE_ROOT="${EVALUATE_ROOT:-$HOME/code/evaluate}"
VALLY="npx --prefix $EVALUATE_ROOT tsx $EVALUATE_ROOT/packages/cli/src/index.ts"
RESULTS_ROOT="${RESULTS_DIR:-$SKILLS_ROOT/vally-results}"
MODEL="${MODEL:-claude-sonnet-4.6}"
JUDGE_MODEL="${JUDGE_MODEL:-claude-sonnet-4.6}"
RUNS="${RUNS:-1}"
WORKERS="${WORKERS:-3}"

PLUGIN="${1:-}"
SKILL="${2:-}"

# Colors
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[0;33m'
BOLD='\033[1m'
NC='\033[0m'

# ---- Discover eval specs ---------------------------------------------------

if [ -n "$SKILL" ] && [ -n "$PLUGIN" ]; then
  EVAL_SPECS=("$SKILLS_ROOT/tests/$PLUGIN/$SKILL/eval.vally.yaml")
elif [ -n "$PLUGIN" ]; then
  EVAL_SPECS=()
  while IFS= read -r f; do EVAL_SPECS+=("$f"); done < <(find "$SKILLS_ROOT/tests/$PLUGIN" -name "eval.vally.yaml" -type f | sort)
else
  EVAL_SPECS=()
  while IFS= read -r f; do EVAL_SPECS+=("$f"); done < <(find "$SKILLS_ROOT/tests" -name "eval.vally.yaml" -type f | sort)
fi

if [ ${#EVAL_SPECS[@]} -eq 0 ]; then
  echo "No eval.vally.yaml files found"
  exit 1
fi

echo -e "${BOLD}Found ${#EVAL_SPECS[@]} eval(s) to run${NC}"
echo ""

# ---- Run each eval ---------------------------------------------------------

PASS=0
FAIL=0
SKIP=0

for EVAL_SPEC in "${EVAL_SPECS[@]}"; do
  EVAL_DIR="$(dirname "$EVAL_SPEC")"
  EVAL_NAME="$(basename "$EVAL_DIR")"
  EVAL_PLUGIN="$(basename "$(dirname "$EVAL_DIR")")"
  SKILL_DIR="$SKILLS_ROOT/plugins/$EVAL_PLUGIN/skills/$EVAL_NAME"

  BASELINE_DIR="$RESULTS_ROOT/$EVAL_PLUGIN/$EVAL_NAME/baseline"
  SKILLED_DIR="$RESULTS_ROOT/$EVAL_PLUGIN/$EVAL_NAME/skilled"
  mkdir -p "$BASELINE_DIR" "$SKILLED_DIR"

  echo -e "${BOLD}━━━ $EVAL_PLUGIN/$EVAL_NAME ━━━${NC}"

  # Check skill directory exists
  if [ ! -d "$SKILL_DIR" ]; then
    echo -e "${YELLOW}⚠ Skill dir not found: $SKILL_DIR — skipping${NC}"
    echo ""
    SKIP=$((SKIP + 1))
    continue
  fi

  # Baseline run (no skill)
  echo -e "  Baseline run..."
  if $VALLY eval \
    --eval-spec "$EVAL_SPEC" \
    --runs "$RUNS" --workers "$WORKERS" \
    --skip-validate \
    --judge-model "$JUDGE_MODEL" \
    --output-dir "$BASELINE_DIR" \
    2>&1 | tee "$BASELINE_DIR/console.log" | grep --line-buffered -E "✔|✘|remaining|error|warning|Graders|passed|failed|Loaded|Saved" | sed 's/^/    /'; then
    echo -e "  ${GREEN}✔ Baseline complete${NC}"
  else
    echo -e "  ${YELLOW}⚠ Baseline had errors${NC}"
  fi

  # Skilled run
  echo -e "  Skilled run..."
  if $VALLY eval \
    --eval-spec "$EVAL_SPEC" \
    --skill-dir "$SKILL_DIR" \
    --runs "$RUNS" --workers "$WORKERS" \
    --skip-validate \
    --judge-model "$JUDGE_MODEL" \
    --output-dir "$SKILLED_DIR" \
    2>&1 | tee "$SKILLED_DIR/console.log" | grep --line-buffered -E "✔|✘|remaining|error|warning|Graders|passed|failed|Loaded|Saved" | sed 's/^/    /'; then
    echo -e "  ${GREEN}✔ Skilled complete${NC}"
  else
    echo -e "  ${YELLOW}⚠ Skilled had errors${NC}"
  fi

  # Adapt results — find JSONL in timestamped subdirs created by --output-dir
  BASELINE_JSONL=$(find "$BASELINE_DIR" -name "*.jsonl" -type f 2>/dev/null | head -1)
  SKILLED_JSONL=$(find "$SKILLED_DIR" -name "*.jsonl" -type f 2>/dev/null | head -1)

  if [ -n "$BASELINE_JSONL" ] && [ -n "$SKILLED_JSONL" ]; then
    node "$SKILLS_ROOT/eng/vally-adapter/adapt.mjs" \
      --baseline "$(dirname "$BASELINE_JSONL")" \
      --skilled "$(dirname "$SKILLED_JSONL")" \
      --skill-name "$EVAL_NAME" \
      --skill-path "plugins/$EVAL_PLUGIN/skills/$EVAL_NAME" \
      --model "$MODEL" \
      --judge-model "$JUDGE_MODEL" \
      --output "$RESULTS_ROOT/$EVAL_PLUGIN/$EVAL_NAME/results.json" \
      2>&1 | sed 's/^/    /'

    # Check verdict
    PASSED=$(node -e "const r=JSON.parse(require('fs').readFileSync('$RESULTS_ROOT/$EVAL_PLUGIN/$EVAL_NAME/results.json','utf-8')); console.log(r.verdicts[0].passed)")
    if [ "$PASSED" = "true" ]; then
      PASS=$((PASS + 1))
    else
      FAIL=$((FAIL + 1))
    fi
  else
    echo -e "  ${RED}✘ Missing JSONL output — adapt skipped${NC}"
    FAIL=$((FAIL + 1))
  fi

  echo ""
done

# ---- Summary ---------------------------------------------------------------

echo -e "${BOLD}━━━ Summary ━━━${NC}"
echo -e "  ${GREEN}✔ $PASS passed${NC}"
[ $FAIL -gt 0 ] && echo -e "  ${RED}✘ $FAIL failed${NC}"
[ $SKIP -gt 0 ] && echo -e "  ${YELLOW}⚠ $SKIP skipped${NC}"
echo -e "  Results: $RESULTS_ROOT"

[ $FAIL -gt 0 ] && exit 1 || exit 0
