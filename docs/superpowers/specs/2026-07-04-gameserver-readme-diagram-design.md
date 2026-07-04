# GameServer README — 클래스 다이어그램 문서화

## 배경

`GameServer`에는 mermaid classDiagram을 기반으로 생성된 23개 도메인 타입(Stats/Combat/Items/
Entities/Systems)이 있다(설계 근거: `plan/gameserver_domain_scaffold_0704.md`). 아직 이 프로젝트에는
README가 없어, 코드만 보고는 전체 타입 관계를 한눈에 파악하기 어렵다.

사용자가 "생성된 스켈레톤을 리드미에 클래스 다이어그램으로 남겨달라"고 요청했다.

## 결정된 방향

- **위치:** `GameServer/README.md` (신규 생성)
- **다이어그램 소스:** 원본 사용자 제공 다이어그램을 그대로 재사용하지 않고, **실제 생성된 C# 코드**를
  기준으로 다시 그린다. 원본은 camelCase 메서드명(`takeDamage`)과 `SlotType`/`DropPool` 미정의 등
  실제 코드와 어긋나는 부분이 있어, 코드와 항상 일치하는 문서가 되도록 코드 기준으로 재작성한다.
- **범위:** 이 다이어그램은 참고용 문서이며 설계 결정 자체는 이미 `plan/` 문서가 담당한다. 별도 plan
  문서를 추가로 만들지 않는다.

## README 구성

1. 한 줄 소개 — GameServer 도메인 모델 스켈레톤이라는 설명, 스켈레톤 상태(로직 미구현) 명시
2. mermaid `classDiagram` 코드 블록 — 실제 코드의 PascalCase 시그니처·필드·상속·연관 관계 반영
3. 폴더 구조 ↔ 다이어그램 섹션(Stats/Combat/Items/Entities/Systems) 대응 설명 한 단락
4. 관련 문서 링크 — `plan/gameserver_domain_scaffold_0704.md`

## 다이어그램에 반영할 실제 코드 구조 (요약)

- `Stats`: `BigNumber`(struct), `StatType`/`ModifierType`(enum), `StatModifier`, `BaseStats`,
  `Traits`, `FinalStats`
- `Combat`: `StatusEffect`, `BuffManager`
- `Items`: `Item`(abstract) ← `Equipment`(abstract) ← `Weapon`/`Armor`/`Accessory`,
  `SlotType`(enum), `EquipmentInventory`
- `Entities`: `Entity`(abstract) ← `Player`/`Monster`
- `Systems`: `RewardComponent`, `LootData`, `DropPool`, `OfflineProgressionManager`

각 클래스의 프로퍼티·메서드 시그니처는 실제 소스 파일(`GameServer/<도메인>/<Class>.cs`)에서 그대로
가져온다 — 새 필드나 메서드를 추가하지 않고 있는 그대로 문서화한다.

## 완료 기준

- `GameServer/README.md`가 생성되고 mermaid 코드 블록이 실제 코드와 1:1로 대응한다.
- 다이어그램 렌더링 문법 오류가 없다 (mermaid 문법 기준 육안 검토).
