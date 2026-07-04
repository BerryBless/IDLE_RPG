# GameServer 도메인 모델 스캐폴딩

## 1. 배경 및 목적

`GameServer` 프로젝트는 `Console.WriteLine("Hello, World!")` 한 줄뿐인 빈 스캐폴드 상태였다.
사용자가 방치형(Idle) RPG의 스탯·전투·아이템·엔티티·보상 시스템을 아우르는 mermaid `classDiagram`을
제공했고, 이를 C# 타입 계층으로 옮겨 향후 로직 구현의 뼈대를 마련하는 것이 목적이다.

이번 사이클의 범위는 **구조 확정**이다. 각 클래스의 시그니처·상속 관계·문서 주석만 완성하고,
실제 계산 로직(BigNumber 연산, 스탯 집계 파이프라인, 드롭 확률 롤 등)은 다음 사이클으로 미룬다.
로직 없이 구조부터 고정하면 이후 여러 사이클(스탯 집계, 전투, 보상)이 동일한 타입 위에서
독립적으로 진행될 수 있다.

## 2. 설계 결정

| 항목 | 채택안 | 대안 | 사유 |
|------|--------|------|------|
| 구현 깊이 | 스켈레톤 (시그니처 + XML 문서, 본문은 `NotImplementedException`) | 핵심 로직 실구현 / 전체 구현 | 다이어그램 구조 확정이 선행 과제. 로직은 스탯 집계·전투·보상 등 각기 다른 후속 사이클에서 다룰 예정이라 지금 섞으면 리뷰 단위가 커짐 |
| 파일 구성 | 1파일 1클래스, 도메인별 폴더(Stats/Combat/Items/Entities/Systems) + 대응 네임스페이스 | 플랫 구조(GameServer 루트에 전부) | 다이어그램의 6개 섹션 구분이 그대로 폴더 경계가 되어 탐색성이 높음 |
| 네이밍 | 메서드·프로퍼티 PascalCase, enum 멤버 PascalCase | 다이어그램 원문(camelCase/UPPER_SNAKE) 그대로 | C# 관례 및 CLAUDE.md 스타일 규칙 준수 (`takeDamage`→`TakeDamage`, `ATK_SPEED`→`AtkSpeed`) |
| 미정의 타입 | `SlotType`(enum), `DropPool`(class) 신규 정의 | 미정의 상태로 두고 주석만 표시 | 다이어그램이 `EquipmentInventory.equip(slot)`, `RewardComponent.dropTable`에서 참조만 하고 정의하지 않음 → 컴파일 완결성을 위해 합리적 최소 형태로 보완 |
| BigNumber | `readonly struct` | `class` | 다이어그램에 `<<Struct / Value Object>>` 명시. 불변 값 타입으로 힙 할당 없이 사용 |
| BaseStats/Traits | `class` + `static operator+` | 레코드(`record`) | 다이어그램은 가변 필드 접근을 전제로 한 클래스 구조이며, 연산자 오버로드는 class에서도 문제없이 동작 |
| OfflineProgressionManager | 일반 `sealed class`, 싱글턴 메커니즘은 보류 | DI 등록형 싱글턴 즉시 구현 | 다이어그램은 `<<System / Singleton>>` 스테레오타입이지만, 이번 사이클은 구조 스캐폴딩이 목적이라 싱글턴 수명 관리(정적 인스턴스 vs DI 컨테이너)는 서버 전체 아키텍처가 확정된 뒤 결정하는 것이 합리적 |

## 3. 컴포넌트 구조

```
GameServer/
├─ Stats/            (namespace GameServer.Stats)
│  ├─ BigNumber.cs        struct: Coefficient(double), Exponent(int); Add, Multiply
│  ├─ StatType.cs         enum: Hp, Atk, Def, Recovery, AtkSpeed, CritProb, CritDmg, ArmorPen, Lifesteal
│  ├─ ModifierType.cs     enum: FlatAdd, PercentAdd, PercentMult
│  ├─ StatModifier.cs     StatType, ModifierType, BigNumber Value
│  ├─ BaseStats.cs        BigNumber Hp/Atk/Def/Recovery; static operator+
│  ├─ Traits.cs           float AtkSpeed/CritProb/CritDmg/ArmorPen/Lifesteal; static operator+
│  └─ FinalStats.cs       BigNumber CurrentHp/MaxHp/Atk/Def/Recovery; Traits CombatTraits
├─ Combat/           (namespace GameServer.Combat)
│  ├─ StatusEffect.cs     EffectId, MaxDuration, TimeRemaining, IsDebuff; Tick, IsExpired, GetModifiers
│  └─ BuffManager.cs      List<StatusEffect>; ApplyEffect, RemoveEffect, Update, GetAllActiveModifiers
├─ Items/            (namespace GameServer.Items)
│  ├─ Item.cs            abstract: InstanceId, ItemMetaId, Name
│  ├─ Equipment.cs       abstract : Item; List<StatModifier> BaseModifiers/RandomModifiers
│  ├─ Weapon.cs         : Equipment; float AttackRange
│  ├─ Armor.cs          : Equipment
│  ├─ Accessory.cs      : Equipment
│  ├─ SlotType.cs       enum: Weapon, Armor, Accessory  (신규)
│  └─ EquipmentInventory.cs  슬롯 필드(private); Equip, Unequip, GetAllModifiers
├─ Entities/         (namespace GameServer.Entities)
│  ├─ Entity.cs         abstract: InstanceId, Level; protected BaseStats/Traits;
│  │                    FinalStats, BuffManager; TakeDamage, Update, UpdateFinalStats,
│  │                    protected abstract GetExtraModifiers
│  ├─ Player.cs        : Entity; AccountId, CurrentExp, CurrentGold, EquipmentInventory; AddExp, AddGold
│  └─ Monster.cs       : Entity; MonsterId, RewardComponent, monsterAffixes(private)
└─ Systems/          (namespace GameServer.Systems)
   ├─ RewardComponent.cs  BigNumber ExpDrop/GoldDrop; List<DropPool> DropTable; GenerateLoot
   ├─ LootData.cs         DTO: TotalExp, TotalGold, List<Item> AcquiredItems
   ├─ DropPool.cs         int ItemMetaId, float DropChance, int MinQty/MaxQty  (신규)
   └─ OfflineProgressionManager.cs  ProcessOfflineTime(Player, Monster, int) → LootData
```

**의존 관계:**

```
Stats  (기반, 무의존)
  ↑
Combat, Items  (Stats 참조)
  ↑
Entities  (Combat, Items, Stats 참조; Monster는 Systems.RewardComponent도 참조)
  ↑
Systems  (Entities, Items, Stats 참조; OfflineProgressionManager가 Player/Monster를 소비)
```

네임스페이스 간 상호 참조(`Entities.Monster` → `Systems.RewardComponent`, `Systems.OfflineProgressionManager`
→ `Entities.Player/Monster`)는 C#에서 어셈블리 순환 문제 없이 정상 컴파일된다.

## 4. 핵심 API

```csharp
namespace GameServer.Stats;

public readonly struct BigNumber
{
    public double Coefficient { get; init; }
    public int Exponent { get; init; }

    public BigNumber Add(BigNumber other) => throw new NotImplementedException();
    public BigNumber Multiply(BigNumber other) => throw new NotImplementedException();
}
```

```csharp
namespace GameServer.Entities;

public abstract class Entity
{
    public string InstanceId { get; init; } = string.Empty;
    public int Level { get; set; }
    protected BaseStats BaseStats { get; set; } = new();
    protected Traits BaseTraits { get; set; } = new();
    public FinalStats FinalStats { get; init; } = new();
    public BuffManager BuffManager { get; init; } = new();

    public void TakeDamage(BigNumber amount) => throw new NotImplementedException();
    public void UpdateFinalStats() => throw new NotImplementedException();
    protected abstract List<StatModifier> GetExtraModifiers();
}
```

사용 패턴(구조 연결 예시, `GameServer/Program.cs`에서 발췌):

```csharp
var monster = new Monster
{
    InstanceId = "monster-0001",
    MonsterId = 1001,
    Level = 1,
    Rewards = new RewardComponent
    {
        ExpDrop = new BigNumber { Coefficient = 1.0, Exponent = 2 },
        GoldDrop = new BigNumber { Coefficient = 5.0, Exponent = 1 },
        DropTable = [ new DropPool { ItemMetaId = 2001, DropChance = 0.1f, MinQty = 1, MaxQty = 1 } ]
    }
};
```

## 5. 변경 파일 목록

**신규 (23개)** — 3번 컴포넌트 구조 트리의 모든 `.cs` 파일. 다이어그램에 없던 신규 타입은
`Items/SlotType.cs`, `Systems/DropPool.cs` 2개.

**수정 (1개)** — `GameServer/Program.cs`: 도메인 타입 인스턴스 생성과 구조 연결만 보여주는 예제로 교체.
`NotImplementedException`을 던지는 메서드는 호출하지 않음.

## 6. 빌드 검증

```powershell
dotnet build GameServer/GameServer.csproj
dotnet run --project GameServer/GameServer.csproj
```

결과: **0 warning, 0 error**. `EquipmentInventory`의 슬롯 필드 3개는 스텁 메서드 본문에서 아직
참조되지 않아 `CS0169` 경고가 발생했으나, 실제 착용 로직 구현 시 사용될 필드임을 명시하는
`#pragma warning disable/restore CS0169` 주석 처리로 해소했다.

실행 결과 예시:
```
Player player-0001 (Lv.1) vs Monster monster-0001 (Lv.1)
Equipped candidate: 낡은 검, range=1.5
Offline system ready: OfflineProgressionManager
```

## 7. 향후 확장 포인트

- `BigNumber` 사칙연산·정규화(coefficient를 [1, 10) 범위로 유지) 실제 구현
- `FinalStats` 스탯 집계 파이프라인: `BaseStats`/`BaseTraits` + 장비/버프 모디파이어를
  `FlatAdd` → `PercentAdd` → `PercentMult` 순으로 적용
- `BuffManager.Update` / `StatusEffect.Tick` 시간 경과·만료 처리
- `EquipmentInventory.Equip/Unequip` 슬롯별 장비 교체 로직 (private 필드 실제 사용 시작)
- `RewardComponent.GenerateLoot` 드롭 확률 롤 + 킬 수 스케일링
- `OfflineProgressionManager.ProcessOfflineTime` 오프라인 보상 공식 확정 시,
  다이어그램의 `<<Singleton>>` 스테레오타입을 실제로 반영할지(정적 인스턴스 vs DI 등록) 별도 결정 필요
- 각 항목이 설계 확정되면 `plan/<기능명>_MMDD.md` 형식으로 후속 문서화
