# 코드리뷰 후속 정리: F6(사망 시 회복 지속) / F8(장비 PercentMult 불일치) / F11(루팅 할당 압박)

## 1. 배경 및 목적

`plan/battle_system_0705.md` 코드리뷰(2026-07-05)에서 F1~F5는 즉시 수정했고, F6/F8/F11은
"다음 BattleLoop 사이클 참고용"으로 남겨뒀다. 사용자가 이 세 항목도 지금 마저 정리해달라고
요청했다. 세 항목은 서로 독립적이며, `BattleLoop` 자체(웨이브/보스/스폰)가 없어도 각각
단독으로 고칠 수 있는 스코프임을 확인했다.

## 2. 설계 결정

| 항목 | 채택안 | 대안 | 사유 |
|------|--------|------|------|
| F6 | `Entity.Update`가 사망(`CurrentHp<=0`) 시 **전체 조기 리턴** | 회복/마나재생만 중지, 버프 틱은 계속 | 시체가 버프 만료·스탯 재계산을 계속 하는 것도 의미 없는 낭비라 완전 정지가 더 단순하고 직관적 |
| F8 | `EquipmentInventory.GetAllModifiers`의 `GroupBy+Sum` 병합 단계 **완전 제거**, 이어붙이기+캐싱만 유지 | `PercentMult`만 병합 제외 | `Entity.UpdateFinalStats`가 이미 StatType별로 Flat/PercentAdd 합산·PercentMult 곱연산을 올바르게 수행하므로 장비 계층의 사전 병합은 중복이자 버그 원인. 코드도 더 짧아짐 |
| F11 | `GenerateLoot`을 **ItemMetaId별 수량 집계**로 변경(kill마다 개별 `LootItem` 생성 대신 `Dictionary`로 누적 후 distinct 아이템당 1개 생성) | killCount 상한 캡 | 오프라인 파밍(F1로 killCount가 커질 수 있음)에서 할당량을 `killCount`가 아닌 `DropTable.Count`에 비례하게 만들어 근본 해결. 캡은 방치형 파밍 시간을 인위적으로 제한하는 부작용이 있어 기각 |

## 3. 컴포넌트 구조

신규 파일 없음. 기존 3개 파일만 수정:

```
GameServer/
├─ Entities/
│  └─ Entity.cs           — Update()에 사망 조기 리턴 가드 + IsAlive 프로퍼티 추가
├─ Items/
│  └─ EquipmentInventory.cs — GetAllModifiers()에서 GroupBy+Sum 병합 단계 삭제
└─ Systems/
   └─ RewardComponent.cs   — GenerateLoot()을 Dictionary 기반 ItemMetaId 집계로 재작성
```

의존 관계 변경 없음(세 파일 모두 기존 인터페이스·시그니처 유지, 내부 구현만 변경).

## 4. 핵심 API

```csharp
// Entities/Entity.cs
public bool IsAlive => FinalStats.CurrentHp > 0;

public void Update(float deltaTime)
{
    if (FinalStats.CurrentHp <= 0)
    {
        return; // 사망 상태: 버프 틱·스탯 재계산·회복 전부 정지 (코드리뷰 F6)
    }

    BuffManager.Update(deltaTime);
    UpdateFinalStats();
    FinalStats.CurrentHp = Math.Min(FinalStats.MaxHp, FinalStats.CurrentHp + FinalStats.Recovery * deltaTime);
    FinalStats.CurrentMana = Math.Min(FinalStats.MaxMana, FinalStats.CurrentMana + FinalStats.ManaRegen * deltaTime);
}
```

```csharp
// Items/EquipmentInventory.cs
public IReadOnlyList<StatModifier> GetAllModifiers()
{
    if (!_isDirty) return _cachedModifiers;

    _cachedModifiers.Clear();
    if (_equippedWeapon != null) _cachedModifiers.AddRange(_equippedWeapon.Modifiers);
    if (_equippedArmor != null) _cachedModifiers.AddRange(_equippedArmor.Modifiers);
    if (_equippedAccessory != null) _cachedModifiers.AddRange(_equippedAccessory.Modifiers);
    _isDirty = false;
    return _cachedModifiers;
}
```

```csharp
// Systems/RewardComponent.cs
public LootData GenerateLoot(int killCount)
{
    killCount = Math.Max(0, killCount);
    var quantityByItemMetaId = new Dictionary<int, int>();

    for (int kill = 0; kill < killCount; kill++)
    {
        foreach (var pool in DropTable)
        {
            if (_random.NextDouble() >= pool.DropChance) continue;
            int quantity = pool.MinQty >= pool.MaxQty ? pool.MinQty : _random.Next(pool.MinQty, pool.MaxQty + 1);
            quantityByItemMetaId[pool.ItemMetaId] = quantityByItemMetaId.GetValueOrDefault(pool.ItemMetaId) + quantity;
        }
    }

    var acquiredItems = quantityByItemMetaId
        .Select(kv => (Items.Item)new LootItem { ItemMetaId = kv.Key, Quantity = kv.Value })
        .ToList();

    return new LootData { TotalExp = ExpDrop * killCount, TotalGold = GoldDrop * killCount, AcquiredItems = acquiredItems };
}
```

## 5. 변경 파일 목록

**수정:** `GameServer/Entities/Entity.cs`, `GameServer/Items/EquipmentInventory.cs`, `GameServer/Systems/RewardComponent.cs`

**테스트 수정/추가 (`tests/GameServer.Tests/`) — 완료:**
- `Entities/EntityRuntimeTests.cs`: `IsAlive`, 사망 시 Update 전체 정지(회복·마나재생·버프 틱
  모두 안 됨), 생존 개체 회귀 확인 테스트 4건 추가
- 신규 `Items/EquipmentInventoryTests.cs`: 동일 무기 내 PercentMult 2건 비병합 확인,
  FlatAdd 소스 합산 확인, 캐싱 유지 확인 3건
- `Systems/RewardComponentTests.cs`: 기존 `GenerateLoot_GuaranteedDrop_AddsOneItemPerKill`,
  `GenerateLoot_QuantityStaysWithinConfiguredRange`를 집계 결과 기대값으로 재작성 + 대량
  killCount(10만)에서도 할당 개수가 DropTable 크기(2)로 유계인지 확인하는 신규 테스트 추가

## 6. 빌드 검증

```powershell
dotnet build IDLE_RPG.sln
dotnet test tests/GameServer.Tests/GameServer.Tests.csproj
dotnet run --project GameServer/GameServer.csproj   # total damage = 99 회귀 확인
```

**실행 결과(2026-07-05):** 솔루션 전체 0 warning / 0 error. `GameServer.Tests` 58/58 통과
(F1~F5 사이클의 50개 + 이번 F6/F8/F11 신규·수정 8건). 기존 `IdleRpg.HarnessTests` 98/98 영향
없음. `Main.cs` 예제 회귀 없음(`total damage = 99` 유지).

## 7. 향후 확장 포인트

- F6의 "완전 정지" 설계는 부활(`RestoreResources` 재호출) 전까지 시체가 그대로 남는다는 전제.
  부활 코스트/타이밍 로직(`ReviveCostCalculator`, 다음 사이클)이 이 지점과 맞물림.
- F11 집계 방식은 "이번 정산에서 어떤 킬이 무엇을 드롭했는지"의 개별 이력을 버린다. 로그/통계가
  필요해지면 별도 이벤트 스트림으로 분리 검토.
