# Framework integrations

Drop-in tools that let agent frameworks use [TrustScoreAgent](https://trustscoreagent.com)
— the free, open reputation registry for AI microservices. Each integration wraps the
public REST API (`https://api.trustscoreagent.com`); **no account or API key is required.**

Every integration exposes the same three tools:

| Tool | When to use |
|------|-------------|
| `trustscore_check_reputation` | **Before** calling a service — get its trust score (0–1), confidence, and availability/latency/conformity breakdown. |
| `trustscore_submit_rating` | **After** calling a service — contribute a rating from your interaction metrics (and an optional receipt). |
| `trustscore_list_services` | Discover reliable services, sorted by trust score. |

## Available integrations

| Framework | Folder | Import |
|-----------|--------|--------|
| **LangChain** | [`langchain/`](./langchain) | `from trustscoreagent_langchain import get_trustscoreagent_tools` |
| **CrewAI** | [`crewai/`](./crewai) | `from trustscoreagent_crewai import get_trustscoreagent_tools` |

Each folder is self-contained (one module + an `example.py` + `requirements.txt`). Copy
the module into your project, or run the example:

```bash
cd langchain          # or: cd crewai
pip install -r requirements.txt
python example.py
```

## Configuration

Both tools read two optional environment variables:

| Variable | Default | Purpose |
|----------|---------|---------|
| `TRUSTSCORE_API_URL` | production API | Point at a different TrustScoreAgent instance. |
| `TRUSTSCORE_AGENT_DID` | auto-generated | Stable identifier for your agent. If unset, a random id is generated once and stored in `~/.trustscoreagent/agent-id` (shared with the MCP server). |

You can also pass `base_url=` / `agent_did=` directly to `get_trustscoreagent_tools(...)`.

## Also available

- **MCP server** (Claude, Cursor, Windsurf, …): `npx -y @trustscoreagent/mcp-server` — see [`../mcp-server`](../mcp-server).
- **Raw REST API**: [`../docs/api.md`](../docs/api.md) and [`../docs/examples.md`](../docs/examples.md).

Contributions adding more frameworks (LlamaIndex, Autogen, …) are welcome — see
[`../CONTRIBUTING.md`](../CONTRIBUTING.md).
