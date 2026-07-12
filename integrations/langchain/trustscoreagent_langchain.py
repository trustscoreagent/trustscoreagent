"""TrustScoreAgent tools for LangChain.

Gives a LangChain agent three tools backed by the free, open reputation registry
at https://api.trustscoreagent.com (no account or API key required):

- ``trustscore_check_reputation`` — check a service's trust score *before* calling it
- ``trustscore_submit_rating``     — rate a service *after* calling it
- ``trustscore_list_services``     — discover reliable services

Usage::

    from trustscoreagent_langchain import get_trustscoreagent_tools

    tools = get_trustscoreagent_tools()
    agent = create_react_agent(llm, tools)          # or bind_tools(tools)

Requires ``langchain-core`` and ``requests`` (see requirements.txt).
"""

from __future__ import annotations

import os
from pathlib import Path
from typing import List, Optional
from uuid import uuid4

import requests
from langchain_core.tools import StructuredTool
from pydantic import BaseModel, Field

DEFAULT_API_BASE_URL = "https://api.trustscoreagent.com"
_DEFAULT_TIMEOUT = 10.0


def _resolve_agent_did() -> str:
    """Return a stable per-machine agent DID.

    Honors ``TRUSTSCORE_AGENT_DID``; otherwise reuses (or creates) the id stored
    in ``~/.trustscoreagent/agent-id`` — the same file the MCP server uses, so an
    install shares one identity across tools. Contains no personal information.
    """
    env = os.environ.get("TRUSTSCORE_AGENT_DID")
    if env:
        return env

    config_dir = Path.home() / ".trustscoreagent"
    id_file = config_dir / "agent-id"
    try:
        if id_file.exists():
            existing = id_file.read_text(encoding="utf-8").strip()
            if existing:
                return existing
    except OSError:
        pass

    did = f"did:web:agent.trustscoreagent.com:py-{uuid4().hex[:8]}"
    try:
        config_dir.mkdir(parents=True, exist_ok=True)
        id_file.write_text(did, encoding="utf-8")
    except OSError:
        pass  # fall back to an ephemeral id if the home dir is not writable
    return did


class _TrustScoreAPI:
    """Thin, dependency-light client returning LLM-friendly text."""

    def __init__(
        self,
        base_url: Optional[str] = None,
        agent_did: Optional[str] = None,
        timeout: float = _DEFAULT_TIMEOUT,
    ) -> None:
        self.base_url = (
            base_url or os.environ.get("TRUSTSCORE_API_URL") or DEFAULT_API_BASE_URL
        ).rstrip("/")
        self.agent_did = agent_did or _resolve_agent_did()
        self.timeout = timeout

    # -- tools -----------------------------------------------------------------

    def check_reputation(self, service: str) -> str:
        try:
            resp = requests.get(
                f"{self.base_url}/v1/score",
                params={"service": service},
                timeout=self.timeout,
            )
            resp.raise_for_status()
            data = resp.json()
        except requests.Timeout:
            return _timeout_msg("check reputation", self.timeout)
        except requests.RequestException as exc:
            return f"Failed to check reputation: {exc}"

        if data.get("known") is False:
            return (
                f"Service {data.get('service', service)}: UNKNOWN (no ratings yet).\n"
                "Neutral score 0.5 (no data). Proceed with caution, and consider "
                "submitting a rating after you call it."
            )

        score = data.get("score", 0.5)
        level = "HIGH" if score >= 0.8 else "MODERATE" if score >= 0.5 else "LOW"
        dims = data.get("dimensions") or {}
        incidents = data.get("recent_incidents", 0) or 0
        lines = [
            f"Trust score for {data.get('service', service)}: {score}/1.0 ({level})",
            f"Confidence: {data.get('confidence', 0)} "
            f"(based on {data.get('ratings_count', 0)} ratings)",
            "Dimensions: "
            f"availability={dims.get('availability', 'n/a')}, "
            f"latency={dims.get('latency', 'n/a')}, "
            f"conformity={dims.get('conformity', 'n/a')}",
            f"{incidents} incident(s) in the last 30 days"
            if incidents
            else "No recent incidents",
            "Supports verified receipts"
            if data.get("service_supports_receipts")
            else "Does not yet support verified receipts",
        ]
        return "\n".join(lines)

    def submit_rating(
        self,
        service: str,
        status_code: int,
        latency_ms: int,
        response_size_bytes: Optional[int] = None,
        schema_valid: Optional[bool] = None,
        quality_score: Optional[int] = None,
        receipt: Optional[str] = None,
    ) -> str:
        metrics = {"status_code": status_code, "latency_ms": latency_ms}
        if response_size_bytes is not None:
            metrics["response_size_bytes"] = response_size_bytes
        if schema_valid is not None:
            metrics["schema_valid"] = schema_valid

        body: dict = {"service": service, "metrics": metrics}
        if quality_score is not None:
            body["quality_score"] = quality_score
        if receipt is not None:
            body["receipt"] = receipt

        try:
            resp = requests.post(
                f"{self.base_url}/v1/rate",
                json=body,
                headers={"X-Agent-DID": self.agent_did},
                timeout=self.timeout,
            )
            if not resp.ok:
                return f"Rating rejected (HTTP {resp.status_code}): {resp.text}"
            data = resp.json()
        except requests.Timeout:
            return _timeout_msg("submit rating", self.timeout)
        except requests.RequestException as exc:
            return f"Failed to submit rating: {exc}"

        return (
            f"Rating submitted for {service}. "
            f"Weight: {data.get('rating_weight', 'unknown')}, "
            f"updated score: {data.get('new_score', 'unknown')}."
        )

    def list_services(
        self,
        sort_by: Optional[str] = None,
        min_score: Optional[float] = None,
        min_ratings: Optional[int] = None,
        limit: Optional[int] = None,
    ) -> str:
        params: dict = {}
        if sort_by is not None:
            params["sort_by"] = sort_by
        if min_score is not None:
            params["min_score"] = min_score
        if min_ratings is not None:
            params["min_ratings"] = min_ratings
        if limit is not None:
            params["limit"] = limit

        try:
            resp = requests.get(
                f"{self.base_url}/v1/services", params=params, timeout=self.timeout
            )
            resp.raise_for_status()
            data = resp.json()
        except requests.Timeout:
            return _timeout_msg("list services", self.timeout)
        except requests.RequestException as exc:
            return f"Failed to list services: {exc}"

        services = data.get("services") or []
        if not services:
            return "No services found matching the criteria."

        rows = []
        for i, svc in enumerate(services, start=1):
            score = svc.get("score", 0.0)
            level = "HIGH" if score >= 0.8 else "MODERATE" if score >= 0.5 else "LOW"
            receipts = " [receipts]" if svc.get("service_supports_receipts") else ""
            rows.append(
                f"{i}. {svc.get('service')} — {score}/1.0 ({level}) — "
                f"{svc.get('ratings_count', 0)} ratings{receipts}"
            )
        count = (data.get("pagination") or {}).get("count", len(services))
        return "\n".join([f"Found {count} service(s):", "", *rows])


def _timeout_msg(action: str, timeout: float) -> str:
    return (
        f"Could not {action}: the TrustScoreAgent API did not respond within "
        f"{timeout:g}s. It may be down or slow — try again later."
    )


# -- argument schemas ----------------------------------------------------------


class CheckReputationInput(BaseModel):
    service: str = Field(
        ...,
        description=(
            "The service to check. Any format works: URL "
            "(https://api.example.com), domain (api.example.com), or DID "
            "(did:web:api.example.com)."
        ),
    )


class SubmitRatingInput(BaseModel):
    service: str = Field(..., description="The service you called (URL, domain, or DID).")
    status_code: int = Field(..., ge=100, le=599, description="HTTP status code returned.")
    latency_ms: int = Field(..., ge=1, le=600000, description="Response time in ms.")
    response_size_bytes: Optional[int] = Field(
        None, ge=0, description="Response size in bytes (optional)."
    )
    schema_valid: Optional[bool] = Field(
        None, description="Whether the response matched the expected format (optional)."
    )
    quality_score: Optional[int] = Field(
        None, ge=1, le=5, description="Subjective quality 1 (poor) to 5 (excellent) (optional)."
    )
    receipt: Optional[str] = Field(
        None, description="JWT from the service's X-Trust-Receipt header (optional)."
    )


class ListServicesInput(BaseModel):
    sort_by: Optional[str] = Field(
        None, description="One of 'score' (default), 'ratings_count', 'last_rated'."
    )
    min_score: Optional[float] = Field(None, ge=0, le=1, description="Minimum trust score.")
    min_ratings: Optional[int] = Field(None, ge=0, description="Minimum number of ratings.")
    limit: Optional[int] = Field(None, ge=1, le=100, description="Max results (default 20).")


def get_trustscoreagent_tools(
    base_url: Optional[str] = None,
    agent_did: Optional[str] = None,
    timeout: float = _DEFAULT_TIMEOUT,
) -> List[StructuredTool]:
    """Return the three TrustScoreAgent tools as LangChain ``StructuredTool``s."""
    api = _TrustScoreAPI(base_url=base_url, agent_did=agent_did, timeout=timeout)
    return [
        StructuredTool.from_function(
            func=api.check_reputation,
            name="trustscore_check_reputation",
            description=(
                "Check the trust score of an AI microservice or public API BEFORE "
                "calling it. Returns score (0-1), confidence, rating count and an "
                "availability/latency/conformity breakdown."
            ),
            args_schema=CheckReputationInput,
        ),
        StructuredTool.from_function(
            func=api.submit_rating,
            name="trustscore_submit_rating",
            description=(
                "Rate an AI microservice AFTER calling it, from your interaction "
                "metrics (status code, latency, optional receipt). Helps other agents."
            ),
            args_schema=SubmitRatingInput,
        ),
        StructuredTool.from_function(
            func=api.list_services,
            name="trustscore_list_services",
            description=(
                "List rated services sorted by trust score, to discover reliable "
                "services or find alternatives."
            ),
            args_schema=ListServicesInput,
        ),
    ]


__all__ = ["get_trustscoreagent_tools"]
