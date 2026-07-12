"""Minimal LangChain example for the TrustScoreAgent tools.

Run:  pip install -r requirements.txt  &&  python example.py

The first part runs without any LLM (it invokes a tool directly, against the
public production API). The commented part shows a full ReAct agent, which needs
an LLM provider key.
"""

from trustscoreagent_langchain import get_trustscoreagent_tools

tools = get_trustscoreagent_tools()
by_name = {t.name: t for t in tools}

# --- No LLM required: call a tool directly -----------------------------------
print("== check_reputation(api.open-meteo.com) ==")
print(by_name["trustscore_check_reputation"].invoke({"service": "api.open-meteo.com"}))

print("\n== list_services(top 5 by score) ==")
print(by_name["trustscore_list_services"].invoke({"limit": 5, "sort_by": "score"}))

# --- Full ReAct agent (needs an LLM provider key) ----------------------------
# from langchain.chat_models import init_chat_model
# from langgraph.prebuilt import create_react_agent
#
# llm = init_chat_model("anthropic:claude-sonnet-5")  # or "openai:gpt-4o", etc.
# agent = create_react_agent(llm, tools)
# result = agent.invoke(
#     {"messages": [("user", "Is api.open-meteo.com reliable enough to call?")]}
# )
# print(result["messages"][-1].content)
