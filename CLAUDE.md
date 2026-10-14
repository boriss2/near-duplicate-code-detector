# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A cross-platform tool (Microsoft Research, Deep Program Understanding group) that detects exact and near-duplicate source files across a code corpus, for deduplicating research datasets. The detector itself is language-agnostic — it operates on pre-tokenized JSONL input, not source code directly. Per-language tokenizers in `tokenizers/` produce that input from real source files.

## Build & run

### Main detector (`DuplicateCodeDetector/`)
Requires .NET Core 2.1+.

```bash
dotnet build DuplicateCodeDetector/DuplicateCodeDetector.csproj
dotnet run --project DuplicateCodeDetector/DuplicateCodeDetector.csproj -- [options] (--dir=<folder> | --input=<file>) <output-file-prefix>
```

Key CLI options (see `CloneDetectorCli.cs` for the full docopt usage string):
- `--dir=<path>` — directory of `.jsonl.gz` files to index, or `--input=<path>` for a single file
- `--id-fields=<fields>` — colon-separated field names forming each entry's identity (default: `filename`)
- `--tokens-field=<name>` — field holding the token list (default: `tokens`)
- `--key-jaccard-threshold` / `--jaccard-threshold` — similarity thresholds (defaults `0.8` / `0.7`)

Output: `<prefix>.log` (pairwise near-duplicate matches with scores) and `<prefix>.json` (transitively-closed clone clusters).

There are no automated tests in this repo — verify changes by running the tool against sample `.jsonl.gz` data and inspecting the `.log`/`.json` output.

### Tokenizers (`tokenizers/`)
Each is a standalone project producing `.jsonl.gz` files consumable by the detector. All take the same positional args: `<projectsFolder> <outputFolder> true|false` (the last flag selects identifiers-only vs. all tokens), except the Python scripts (docopt-based CLIs).

- **C#** (`CsharpTokenizer/`): `dotnet run --project tokenizers/CsharpTokenizer/CsharpTokenizer/CsharpTokenizer.csproj -- <projectsFolder> <outputFolder> true|false` — uses Roslyn (`Microsoft.CodeAnalysis.CSharp`) to tokenize each `.cs` file under each immediate subdirectory of `<projectsFolder>` (each subdirectory = one project → one output `.jsonl.gz`).
- **F#** (`FSharpTokenizer/`): same CLI shape; uses `FSharp.Compiler.Service`'s line tokenizer.
- **Java** (`tokenizers/java/`, Maven): `mvn package` then run `javatokenizer.Extractor`; uses JavaParser, same per-subdirectory project convention.
- **JavaScript** (`tokenizers/javascript/parser.js`): `npm install` then `node parser.js <projectsFolder> <outputFolder>`; uses `esprima`. No identifiers-only flag — always extracts all tokens (hardcoded `false` in `extractForFolder` call).
- **Python** (`tokenizers/python/`): two independent scripts — `tokenizepythoncorpus.py` (stdlib `tokenize`, multiprocessing, per-project-folder) and `baronetokenizer.py` (regex-based, for the Barone et al. code-docstring corpus format specifically). Both depend on `dpu_utils` and `docopt`.

## Architecture

### Data flow
1. A tokenizer (any language) walks a `<projectsFolder>`, treating each immediate subdirectory as one "project," and emits one gzipped JSONL file per project into `<outputFolder>`. Each line is `{"filename": "<relative path>", "tokens": ["...", "..."]}`.
2. `CloneDetector.BuildIndexForProjects`/`BuildIndexFromFiles` reads all `.jsonl.gz` files, and for each file builds a `SparseVector` (token → count) keyed by the `--id-fields`-derived identifier. Files with fewer than `MIN_NUM_TOKENS_FOR_FILE` (20) tokens are skipped entirely.
3. `CloneDetector.FindNearDuplicates` compares every pair of projects (including each project against itself) in parallel (`AsParallel()`), and within each project pair, every file pair, computing two similarity scores from `SparseVector`:
   - `KeyJaccardSimilarity` — Jaccard over the *set* of distinct tokens (ignores counts) — a cheap pre-filter (`--key-jaccard-threshold`).
   - `JaccardSimilarity` — Jaccard over the token *multiset* (min/max of counts per token) — the real similarity measure (`--jaccard-threshold`).
   A pair only proceeds to the (costlier) multiset Jaccard if it passes the set-Jaccard threshold first.
4. Once a file is matched as a duplicate of an earlier-seen file, it's added to `_alreadyDuplicatedFiles` and excluded from further comparison as either side of a pair — this bounds work but means duplicate detection is not symmetric/exhaustive once a file has already been "claimed."
5. Raw pairwise matches accumulate as directed edges in `CloneDetector.Duplicates`. `CloneGroups` turns these into clone clusters: it builds a bidirectional adjacency map, then repeatedly unions each file's clone-set with its neighbors' clone-sets until fixed point (`MakeCloneSetTransitive`) — i.e., near-duplicate clusters are the connected components of the pairwise-match graph, not just the raw pairs. It also logs cluster-size statistics (avg/median duplication factor).
6. `CloneGroups.SaveToJson` serializes the final list of clusters (each a set of file identifiers) to the output `.json`.

### Key implementation details
- `FeatureDictionary` interns tokens to integer IDs so `SparseVector` can use `Dictionary<int,int>` instead of `Dictionary<string,int>` — this is shared across all projects/files to keep IDs consistent.
- Token counting treats tokens as a multiset per file (`Count` in `CloneDetector.cs`), not a sequence — ordering/position of tokens is irrelevant to similarity.
- Parallelism is applied at three levels: across project-pairs, across files-within-a-project-pair, and (via `ConcurrentDictionary`/`[MethodImpl(Synchronized)]`) for shared mutable state (`_alreadyDuplicatedFiles`, `Duplicates`).
- The detector is intentionally tokenizer-agnostic: nothing in `DuplicateCodeDetector/` assumes a particular language. Adding support for a new language means only adding a new tokenizer that emits the same JSONL schema.
