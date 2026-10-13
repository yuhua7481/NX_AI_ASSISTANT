# NX AI Assistant — Project Rules

## Product goal
Build a self-contained Siemens NX2512 in-process AI assistant plugin. The user opens a modeless chat window from a button/menu in the NX main UI, configures a model API, types natural-language modeling requests, and sees changes made directly to the active NX part and feature tree.

## Clean-room rebuild rules
- New workspace: `D:\AI\NX_AI_ASSISTANT`.
- Legacy workspace `D:\AI\NX_MCP` is read-only reference only.
- Do not copy/paste/reuse legacy Engine, MCP, GUI Bridge, generator, or server implementation source as the new implementation. You may inspect old source, test evidence, paths, lessons learned, and API facts; reimplement cleanly in this repository.
- No project dependency on MCP, GUI Bridge, an external coding agent, a separate local service, or an out-of-process NX session.
- The plugin itself calls the configured model API directly.
- Preserve current user's model/provider setup outside this repository; do not change it.

## Product decisions
- NX main interface entry (menu/button) opens a modeless floating chat window. Native docking is not required.
- Chat window can be moved beside NX; NX remains interactive while it is open.
- Repeated clicks focus/reuse the same window instead of spawning duplicates.
- The window and plugin lifetime are tied to the NX process/session; close cleanly when NX exits.
- API endpoint, model name, API key, and request settings are configurable. Design a provider interface and begin with OpenAI-compatible HTTP APIs; do not hard-code a single provider.
- API keys must not be committed or logged. Prefer Windows DPAPI or Credential Manager for stored secrets.
- File attachments should be staged behind a provider capability abstraction (text/document first; image/multimodal only when provider supports it).
- Initial product scope is part modeling and active-part inspection. Assembly comes later; simulation is explicitly later.

## Architecture guardrails
- Keep UI, model-provider transport, conversation/tool orchestration, and NXOpen integration separate.
- HTTP/network work must not block the NX UI thread.
- NXOpen model reads and mutations must run on the valid NX execution/UI thread using a verified in-process dispatch strategy.
- Tool calls must be typed, validated, logged, and limited to registered operations. Avoid arbitrary C# execution from model output.
- Before modifying geometry, inspect the current part and validate required bodies/faces/points/parameters. Ask the user when a required reference or intent is ambiguous.
- Verify postconditions by querying NX, not by trusting the LLM's text.
- Prefer transactions/undo marks where supported and record failure context.
- No model mutation during tests unless the test explicitly uses a disposable test part.
- No use of `run_journal.exe` as the main interactive product path. It can remain a validation tool only.

## Runtime and future-version compatibility rules
- NX2512 is the first baseline target, not the only intended supported release. The product must be designed for planned upgrades to future higher NX/Designcenter releases.
- Never assume one compiled plugin DLL can run against every NX release. The in-process NXOpen adapter must be built and validated against the exact target release/runtime where required; keep shared domain/provider/tool-contract code isolated from version-specific NXOpen integration.
- At startup/install/update, detect the actual NX release, NXOpen assembly version/location, and host runtime. Select an explicitly supported compatibility profile; if unknown, do not silently enable model-mutating tools.
- Use a version/capability profile or adapter registry rather than spreading release-number checks throughout business logic. Discover capabilities where possible, and declare unsupported features explicitly.
- Keep user configuration and API secrets outside versioned plugin binaries/install directories. Version and migrate configuration schemas without losing provider settings, chat history, or user files.
- Installer/updater must detect multiple side-by-side NX installations and install the matching adapter per supported release without replacing another release's files. Support rollback and preserve settings.
- For each new NX release, inspect release notes and available NXOpen API migration tools/reports (including NXOpenReporter when available), rebuild against that release's own references, run automated tests, then perform actual in-process load and disposable-part integration tests before marking it supported.
- Maintain a support matrix with states such as `planned`, `built`, `loaded`, `integration-tested`, and `supported`. Never describe a future release as supported just because the code compiles.
- Do not guess target framework or NXOpen assembly path. Inspect the installed target environment and official/local examples first.
- Do not mix assemblies from different NX runtime/load contexts.
- A passing unit test or DLL compilation does not prove the plugin loads or a model operation works in NX.

## Workflow
1. Inspect the current workspace and `git status` before edits.
2. Implement in small vertical slices and keep the app buildable.
3. Add repeatable tests for logic that does not need NX.
4. For NX-dependent behavior, explicitly distinguish mock tests from real NX acceptance.
5. Back up or preserve user data; no destructive cleanup, reset, or commit unless asked.
6. Report file changes, commands run, exact results, and remaining unverified assumptions.
