from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path


TABLE = "FASTDOM_FOOTPRINT"


def args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--action", choices=("upsert", "load"), required=True)
    return parser.parse_args()


def connect():
    root = Path(os.getenv("FASTDOM_AGENTQUANT_ROOT", r"E:\AIWork\agentquant"))
    sys.path.insert(0, str(root))
    from trading_agent.brokers.schwab_auth import connect_derby
    return connect_derby()


def ensure_table(cursor, schema: str):
    sql = f"""
        CREATE TABLE {schema}.{TABLE} (
            SYMBOL VARCHAR(32) NOT NULL,
            BAR_TIME TIMESTAMP NOT NULL,
            PRICE DECIMAL(20,8) NOT NULL,
            BID_VOLUME BIGINT NOT NULL DEFAULT 0,
            ASK_VOLUME BIGINT NOT NULL DEFAULT 0,
            UNKNOWN_VOLUME BIGINT NOT NULL DEFAULT 0,
            TRADE_COUNT INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (SYMBOL, BAR_TIME, PRICE)
        )
    """
    try:
        cursor.execute(sql)
    except Exception as exc:
        if "X0Y32" not in str(exc).upper() and "ALREADY EXISTS" not in str(exc).upper():
            raise


def derby_timestamp(value: str) -> str:
    dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if dt.tzinfo is not None:
        dt = dt.astimezone(timezone.utc).replace(tzinfo=None)
    return dt.strftime("%Y-%m-%d %H:%M:%S")


def upsert(conn, cursor, schema: str, rows: list[dict]):
    update = f"""UPDATE {schema}.{TABLE}
        SET BID_VOLUME=BID_VOLUME+?, ASK_VOLUME=ASK_VOLUME+?,
            UNKNOWN_VOLUME=UNKNOWN_VOLUME+?, TRADE_COUNT=TRADE_COUNT+?
        WHERE SYMBOL=? AND BAR_TIME=? AND PRICE=?"""
    insert = f"""INSERT INTO {schema}.{TABLE}
        (SYMBOL,BAR_TIME,PRICE,BID_VOLUME,ASK_VOLUME,UNKNOWN_VOLUME,TRADE_COUNT)
        VALUES (?,?,?,?,?,?,?)"""
    for row in rows:
        symbol = row["symbol"].strip().upper()
        bar_time = derby_timestamp(row["barTimeUtc"])
        price = str(row["price"])
        bid, ask = int(row["bidVolume"]), int(row["askVolume"])
        unknown, count = int(row["unknownVolume"]), int(row["tradeCount"])
        cursor.execute(update, (bid, ask, unknown, count, symbol, bar_time, price))
        if cursor.rowcount == 0:
            cursor.execute(insert, (symbol, bar_time, price, bid, ask, unknown, count))
    conn.commit()
    return {"status": "ok", "rows": len(rows)}


def load(cursor, schema: str, payload: dict):
    symbol = str(payload.get("symbol", "")).strip().upper()
    limit = max(1, min(100000, int(payload.get("limit", 10000))))
    sql = f"""SELECT SYMBOL,BAR_TIME,PRICE,BID_VOLUME,ASK_VOLUME,UNKNOWN_VOLUME,TRADE_COUNT
        FROM {schema}.{TABLE} WHERE SYMBOL=? ORDER BY BAR_TIME DESC, PRICE DESC
        FETCH FIRST {limit} ROWS ONLY"""
    cursor.execute(sql, (symbol,))
    rows = []
    for symbol, bar_time, price, bid, ask, unknown, count in cursor.fetchall():
        rows.append({
            "symbol": str(symbol),
            "barTimeUtc": bar_time.replace(tzinfo=timezone.utc).isoformat().replace("+00:00", "Z"),
            "price": float(price),
            "bidVolume": int(bid),
            "askVolume": int(ask),
            "unknownVolume": int(unknown),
            "tradeCount": int(count),
        })
    return {"status": "ok", "rows": rows}


def main():
    parsed = args()
    payload = json.load(sys.stdin)
    schema = os.getenv("DERBY_AUTH_SCHEMA", "ROCH").strip().upper() or "ROCH"
    conn = connect()
    try:
        cursor = conn.cursor()
        ensure_table(cursor, schema)
        result = upsert(conn, cursor, schema, payload) if parsed.action == "upsert" else load(cursor, schema, payload)
        print(json.dumps(result, separators=(",", ":")))
    finally:
        conn.close()


if __name__ == "__main__":
    main()
