#!/usr/bin/env python3
import argparse
import json
import random
import socket
import sys
import time
from typing import Any, Dict, Optional


CASE_NAME_BY_KEY = {
    "1": "AtomicFrame",
    "2": "FragmentedFrame",
    "3": "IncompleteFrame",
    "4": "CoalescedFrames",
}

RES_SEQUENCE = [0, 1, 2, 99]


def build_sample_message(seq_no: int, res: int) -> Dict[str, Any]:
    return {
        "res": res,
        "driver_instance_id": 1,
        "seq_no": seq_no,
        "pub_timestamp": int(time.time() * 1000),
        "command": random.choice([0, 3]),
        "measure": random.choice([0, 1]),
        "work_type": random.choice([0, 1, 2]),
        "payload": {
            "fork_height_mm": random.randint(0, 1500),
            "fork_forward_mm": random.randint(0, 3000),
            "note": "hello_tablet",
        },
    }


def encode_message_bytes(seq_no: int, res: int, delimiter: str) -> bytes:
    msg = build_sample_message(seq_no, res)
    body = json.dumps(msg, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    if delimiter == "newline":
        body += b"\n"
    return body


def send_all(sock: socket.socket, data: bytes) -> None:
    total = 0
    while total < len(data):
        sent = sock.send(data[total:])
        if sent <= 0:
            raise RuntimeError("socket send returned 0/negative")
        total += sent


def split_into_n_chunks(data: bytes, chunk_count: int) -> list[bytes]:
    if chunk_count <= 1:
        return [data]

    length = len(data)
    if length == 0:
        return [b""]

    # chunk_count가 데이터 길이보다 크면 빈 chunk가 생기므로 상한 제한
    chunk_count = min(chunk_count, length)

    base = length // chunk_count
    remainder = length % chunk_count

    chunks: list[bytes] = []
    offset = 0
    for i in range(chunk_count):
        size = base + (1 if i < remainder else 0)
        chunks.append(data[offset:offset + size])
        offset += size
    return chunks

def send_chunked(sock: socket.socket, data: bytes, min_chunk: int, max_chunk: int, jitter_ms: int) -> int:
    """
    min_chunk/max_chunk를 '덩어리 개수' 범위로 해석.
    실제 사용된 chunk_count를 반환한다.
    """
    if min_chunk <= 0:
        raise ValueError("min_chunk must be >= 1")
    if max_chunk < min_chunk:
        raise ValueError("max_chunk must be >= min_chunk")

    chunk_count = random.randint(min_chunk, max_chunk)

    # data를 chunk_count개로 분할
    length = len(data)
    if chunk_count > length:
        chunk_count = length

    base = length // chunk_count
    remainder = length % chunk_count

    offset = 0
    for i in range(chunk_count):
        size = base + (1 if i < remainder else 0)
        chunk = data[offset:offset + size]
        send_all(sock, chunk)
        offset += size

        if jitter_ms > 0 and i != chunk_count - 1:
            time.sleep(random.uniform(0.0, jitter_ms / 1000.0))

    return chunk_count

def connect_with_retry(host: str, port: int) -> socket.socket:
    while True:
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sock.settimeout(3.0)
            sock.connect((host, port))
            sock.settimeout(None)
            return sock
        except Exception as e:
            print(f"[publisher] connect failed: {e} -> retry in 1s", file=sys.stderr)
            try:
                sock.close()
            except Exception:
                pass
            time.sleep(1.0)


def send_case_once(
    sock: socket.socket,
    case_key: str,
    seq_no: int,
    res: int,
    delimiter: str,
    min_chunk: int,
    max_chunk: int,
    jitter_ms: int,
) -> int:
    case_name = CASE_NAME_BY_KEY[case_key]

    if case_key == "1":
        payload = encode_message_bytes(seq_no, res, delimiter)
        print(f"[publisher] {case_name} res={res} seq_no={seq_no} bytes={len(payload)}")
        send_all(sock, payload)
        return seq_no + 1

    if case_key == "2":
        payload = encode_message_bytes(seq_no, res, delimiter)
        actual_chunk_count = send_chunked(sock, payload, min_chunk, max_chunk, jitter_ms)
        print(
            f"[publisher] {case_name} res={res} seq_no={seq_no} "
            f"bytes={len(payload)} chunk_count={actual_chunk_count}"
        )
        return seq_no + 1

    if case_key == "3":
        payload_full = encode_message_bytes(seq_no, res, delimiter)
        cut = max(1, len(payload_full) - random.randint(1, 12))
        payload = payload_full[:cut]
        print(
            f"[publisher] {case_name} PARTIAL res={res} seq_no={seq_no} "
            f"bytes={len(payload)} (cut from {len(payload_full)})"
        )
        send_all(sock, payload)
        return seq_no + 1

    if case_key == "4":
        payload1 = encode_message_bytes(seq_no, res, delimiter)
        payload2 = encode_message_bytes(seq_no + 1, res, delimiter)
        payload = payload1 + payload2
        print(
            f"[publisher] {case_name} CONCAT res={res} "
            f"seq_no={seq_no},{seq_no+1} bytes={len(payload)}"
        )
        send_all(sock, payload)
        return seq_no + 2

    raise ValueError("Invalid case key")


def main() -> int:
    ap = argparse.ArgumentParser(description="Interactive TCP JSON publisher.")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8051)
    ap.add_argument("--delimiter", choices=["none", "newline"], default="none")
    ap.add_argument("--min-chunk", type=int, default=1)
    ap.add_argument("--max-chunk", type=int, default=16)
    ap.add_argument("--jitter-ms", type=int, default=5)
    args = ap.parse_args()

    print("[publisher] 1=AtomicFrame 2=FragmentedFrame 3=IncompleteFrame 4=CoalescedFrames")
    print("[publisher] res cycles as [0,1,2,99] by input count")
    print("[publisher] Ctrl+D to exit.")

    seq_no = 1
    input_count = 0
    sock: Optional[socket.socket] = None

    while True:
        line = sys.stdin.readline()
        if line == "":
            print("[publisher] EOF received. exit.")
            break

        key = line.strip()
        if key not in CASE_NAME_BY_KEY:
            print("[publisher] input must be 1/2/3/4")
            continue

        input_count += 1
        res = RES_SEQUENCE[(input_count - 1) % len(RES_SEQUENCE)]

        if sock is None:
            sock = connect_with_retry(args.host, args.port)
            print(f"[publisher] connected to {args.host}:{args.port}")

        try:
            seq_no = send_case_once(
                sock,
                key,
                seq_no,
                res,
                args.delimiter,
                args.min_chunk,
                args.max_chunk,
                args.jitter_ms,
            )
        except Exception as e:
            print(f"[publisher] connection lost: {e}", file=sys.stderr)
            try:
                sock.close()
            except Exception:
                pass
            sock = None

    if sock:
        try:
            sock.close()
        except Exception:
            pass

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
