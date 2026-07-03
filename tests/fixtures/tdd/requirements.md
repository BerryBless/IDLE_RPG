# 요구사항 (테스트 픽스처)

`Calculator` 클래스를 TDD로 구현한다.

## 공개 API
- `int Add(int a, int b)` — 두 정수의 합을 반환한다.
- `int Divide(int a, int b)` — a를 b로 나눈 몫(정수)을 반환한다. `b == 0`이면
  `DivideByZeroException`을 던진다.

## 테스트 케이스 요구사항
- Add: 두 양수, 음수 포함 케이스
- Divide: 정상 나눗셈 1개 이상, `b == 0`일 때 `DivideByZeroException` 발생 확인

## 비고
이 요구사항은 tdd-orchestrator 하네스의 계층2 기능 픽스처다. 의도적으로 모호한 분기가 없도록
작성했다(analyst가 "요구사항 불명확" 질문 루프로 빠지지 않도록) — Red→Green→Refactor 파이프라인이
`dotnet test` green까지 엔드투엔드로 도달하는지 확인하는 것이 목적이다.
