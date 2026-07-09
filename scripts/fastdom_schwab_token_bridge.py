from __future__ import annotations

import argparse
import json
import os
import sys
import traceback
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Return a Schwab access token using AgentQuant auth.")
    parser.add_argument("--include-access-token", action="store_true")
    parser.add_argument("--derby-jdbc-url", default="")
    parser.add_argument("--derby-user", default="")
    parser.add_argument("--derby-password", default="")
    parser.add_argument("--derby-schema", default="")
    parser.add_argument("--schwab-purpose", default="")
    parser.add_argument("--schwab-account-id", default="")
    parser.add_argument("--agentquant-root", default="")
    return parser.parse_args()


def resolve_agentquant_root(configured: str) -> Path:
    candidates = []
    if configured:
        candidates.append(Path(configured))
    env_root = os.getenv("FASTDOM_AGENTQUANT_ROOT")
    if env_root:
        candidates.append(Path(env_root))
    candidates.append(Path(r"E:\AIWork\agentquant"))

    for candidate in candidates:
        root = candidate.expanduser().resolve()
        if (root / "trading_agent" / "brokers" / "schwab_auth.py").is_file():
            return root
    raise RuntimeError("Could not find AgentQuant root with trading_agent/brokers/schwab_auth.py")


def set_if_value(name: str, value: str) -> None:
    value = (value or "").strip()
    if value:
        os.environ[name] = value


def main() -> int:
    args = parse_args()
    root = resolve_agentquant_root(args.agentquant_root)
    sys.path.insert(0, str(root))

    set_if_value("DERBY_JDBC_URL", args.derby_jdbc_url)
    set_if_value("DERBY_USER", args.derby_user)
    set_if_value("DERBY_PASSWORD", args.derby_password)
    set_if_value("DERBY_AUTH_SCHEMA", args.derby_schema)
    set_if_value("SCHWAB_AUTH_SCHEMA", args.derby_schema)
    set_if_value("SCHWAB_AUTH_PURPOSE", args.schwab_purpose)
    set_if_value("SCHWAB_ACCOUNT_ID", args.schwab_account_id)
    os.environ["SCHWAB_AUTH_SOURCE"] = "derby"

    from trading_agent.brokers.schwab_auth import SchwabAuthConfig, get_access_token

    config = SchwabAuthConfig.from_env()
    token = get_access_token(config)
    payload = {
        "status": "ok",
        "authenticated": bool(token),
        "expires_in": 1800,
        "config": config.sanitized(),
    }
    if args.include_access_token:
        payload["access_token"] = token
    print(json.dumps(payload, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(json.dumps({
            "status": "error",
            "error": str(exc),
            "traceback": traceback.format_exc(),
        }, sort_keys=True), file=sys.stderr)
        raise SystemExit(1)
