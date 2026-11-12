---
name: monica-infra-ai
description: Configure and use Monica AI providers, chat, knowledge bases, RAG, runtime skills, and MCP in an application. Use when composing AI capabilities or diagnosing their provider and storage choices.
---

# Monica AI integration

Choose conversational runs, document inventory or retrieval, and agent tool exposure separately.

| Task | Read |
| --- | --- |
| Chat from code or the AI UI, select a provider, or manage sessions and attachments | [Chat and providers](references/chat-and-providers.md) |
| Maintain document inventory without semantic search | [Knowledge and RAG](references/knowledge-and-rag.md) |
| Index documents, search vectors, or connect knowledge retrieval to chat | [Knowledge and RAG](references/knowledge-and-rag.md) |
| Make class or file skills available inside an agent | [Runtime skills](references/runtime-skills.md) |
| Expose tools through MCP or connect an external MCP server | [MCP](references/mcp.md) |

For shell hosting, use UI in `$monica-infra-ui`. For implementing Monica modules, use `$monica-framework`, then `$monica-development`.

If a named skill is unavailable, use `$monica-guide` to resolve the verified Monica checkout (`source resolve --repository monica --json`) and read its canonical `skills/<name>/SKILL.md` and specified reference.
