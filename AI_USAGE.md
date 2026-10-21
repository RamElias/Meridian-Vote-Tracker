# AI usage

Codex (GPT-6) was used as a development assistant to inspect the supplied assignment and planning documents, propose the initial solution structure, and draft the first infrastructure files: the API and Worker entry points, PostgreSQL migration/bootstrap code, Compose configuration, and project documentation.

The generated work was checked against the primary assignment PDF and the existing `DECISIONS.md`. A first OpenAPI draft used .NET 9-style registration methods that do not exist in .NET 8; this was caught by compilation and replaced with a small explicit OpenAPI document. The design keeps API and Worker as separate processes, uses PostgreSQL as the persistent source of truth, uses SQL migrations and a repeatable seed, stages CSV rows in bounded binary COPY batches, and keeps credentials in local environment configuration. The appendix PDF was treated as illustrative only.

No AI model or document extraction service is part of the application runtime. The code compiles with .NET SDK 8.0.426 (zero warnings). Running the CSV smoke-test executable was blocked by Windows Application Control, and Docker Compose/PostgreSQL integration verification remains unrun because this machine session has no Docker CLI or PostgreSQL server.

