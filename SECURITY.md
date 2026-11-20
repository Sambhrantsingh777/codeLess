# Security Policy

## Local-First & Bring-Your-Own-Key (BYOK) Architecture

`codeLess` is designed with strict local-first data ownership:
- **Zero Intermediary Servers**: Prompts, repository maps, and code context are sent directly over HTTPS from your machine to your configured provider endpoint (Anthropic, OpenAI, Google Gemini, OpenRouter, DeepSeek, Groq, Mistral, xAI, or local `127.0.0.1` Ollama / vLLM / LM Studio).
- **Zero Telemetry**: `codeLess` does not collect analytics, usage telemetry, or source code snippets.
- **Tool-Execution Approval Gate**: File mutations (`write_file`, `edit_file`) and shell commands (`run_terminal`) support user approval gating (`RequireAgentToolApproval`) and stage edits in memory as reviewable diffs before touching disk.
- **Built-In Secret & Sink Diagnostics**: `LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics` flags hardcoded secret patterns (`sk-...`, `ghp_...`, `AKIA...`) and dynamic execution sinks (`eval` / `exec`) locally in real time.

## Supported Versions

| Version | Supported |
| :--- | :--- |
| `3.2.x` | Yes |
| `< 3.2.0` | No |

## Reporting a Vulnerability

If you discover a security vulnerability (such as command-injection, path-traversal, or extension-host sandbox escape), please **do not** open a public issue with exploit details.

Instead, report it privately via [GitHub Security Advisories](https://github.com/Sambhrantsingh777/codeLess/security/advisories/new) on [`Sambhrantsingh777/codeLess`](https://github.com/Sambhrantsingh777/codeLess) with:
1. Affected file(s) and platform (Windows WinForms, macOS/Linux Avalonia, or `codeLess.Core`).
2. Minimal reproduction steps.
3. Impact assessment and suggested remediation (if any).
