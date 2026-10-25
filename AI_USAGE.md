# AI usage

## Tools and purpose

**Codex (GPT-6)** assisted with requirements analysis, architecture alternatives, C#/SQL/Angular implementation, OpenAPI client integration, regression tests, code review and documentation. AI-assisted review findings were treated as hypotheses and checked against the assignment and implementation before changes were made. The primary requirements took precedence over the illustrative screen appendix.

AI was used throughout development, including substantial implementation work. Its output was not assumed correct. The project decisions and verification evidence are recorded in [DECISIONS.md](DECISIONS.md) and [VERIFICATION.md](VERIFICATION.md).

## How generated work was checked

- Built the API/Worker Docker images and the Angular production bundle.
- Ran regression checks against real, isolated PostgreSQL, including actual HTTP API and Worker processes, concurrent writes, process interruption/recovery and frozen snapshots.
- Ran Angular tests for screen state, PDF review and late asynchronous responses; checked generated-client consistency with OpenAPI.
- Exercised workflows manually in the UI/API and inspected results from supplied CSV files and real PDF extractions.

The latest local checks passed: PostgreSQL quick integration suite, 12 Angular tests, generated-client consistency and production build. Detailed scale measurements and testing limitations belong in VERIFICATION.md rather than this usage summary. Tests are evidence for the covered scenarios, not proof of correctness for every input.

## Examples where AI-assisted output was corrected

| Initial output or assumption | Evidence and correction |
|---|---|
| A CSV smoke check assumed the supplied file contained no malformed records. | A real five-field row disproved that assumption. The check now verifies preservation/reporting of the bad row and continued parsing. |
| Initial grouped import SQL relied on incorrect visibility assumptions between data-modifying CTEs. | Executing the workflow against PostgreSQL exposed the issue; SQL was corrected and regression coverage retained. |
| The PDF parser assumed a fixed table width and interpreted checkbox tokens in otherwise empty rows as country codes. | Stored real Azure responses contained merged member-name columns. Parsing now respects column spans and skips checkbox-only identities; regression fixtures reproduce both cases. |
| Synthetic PDF tests did not expose all layout problems, and low-confidence empty checkbox cells distorted row confidence. | Real extraction review led to relevant-span confidence calculation and safer reparse of untouched drafts. Ambiguous ballots remain flagged for a human rather than being guessed. |

## Development AI versus application AI

The CSV, conflict and ranking workflows use no runtime AI model. The optional PDF feature calls **Azure Document Intelligence**, without an LLM. Its output remains untrusted: deterministic validation, source comparison and explicit human approval precede the ordinary CSV pipeline. Source PDFs, raw responses and review history are retained.

Automated PDF tests use simulated responses and a recording HTTP handler. Two user-initiated real extractions informed parser fixes, but accuracy across the six supplied scans has not been independently measured. Live connectivity for the other Azure providers is not claimed. Secrets remain outside Git in ignored local configuration or Key Vault.
