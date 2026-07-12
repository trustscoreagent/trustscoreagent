"""Minimal CrewAI example for the TrustScoreAgent tools.

Run:  pip install -r requirements.txt  &&  python example.py

The first part runs without any LLM (it invokes a tool directly, against the
public production API). The commented part shows a full crew, which needs an LLM
provider key.
"""

from trustscoreagent_crewai import get_trustscoreagent_tools

tools = get_trustscoreagent_tools()
by_name = {t.name: t for t in tools}

# --- No LLM required: call a tool directly -----------------------------------
print("== check_reputation(api.open-meteo.com) ==")
print(by_name["trustscore_check_reputation"].run(service="api.open-meteo.com"))

print("\n== list_services(top 5 by score) ==")
print(by_name["trustscore_list_services"].run(limit=5, sort_by="score"))

# --- Full crew (needs an LLM provider key) -----------------------------------
# from crewai import Agent, Task, Crew
#
# scout = Agent(
#     role="Service scout",
#     goal="Only recommend trustworthy APIs to call",
#     backstory="You vet external services before the team spends a call on them.",
#     tools=tools,
# )
# task = Task(
#     description="Check whether api.open-meteo.com is trustworthy enough to call.",
#     expected_output="A one-line recommendation with the trust score.",
#     agent=scout,
# )
# print(Crew(agents=[scout], tasks=[task]).kickoff())
