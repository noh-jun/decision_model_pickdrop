# 실행 방법

```
# 가상 머신 실행
python3 -m venv .venv
sourve .venv/bin/activate

# Chunk 분할 전송 옵션을 추가하여 실행 (최소 분할 2(EA) / 최대 분할 7(EA))
python3 tablet_publisher.py --min-chunk 2 --max-chunk 7

# 실행 후 packet 전송 방법
1 + Enter : Atomic Frame (혼전한 Frame 을 전송하여 파싱이 동작하는지 검증 용도)
2 + Enter : Fragmented Frame (random chunk 개수로 분할 전송하여 링버퍼에서 Frame 통합 + 파싱이 동작하는지 검증 용도)
3 + Enter : Incomplete Frame (불완전한 Frame 을 전송하여 Frame 예외 처리가 동작하는지 검증 용도)
4 + Enter : Coalesced Frames (다수의 Frame 을 전송하여 2개 이상의 Frame 연속 처리가 동작하는지 검증 용도)
