using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// 몬스터 마스터 데이터 테이블. 현재는 11종을 C#으로 하드코딩해 보관한다.
/// </summary>
/// <remarks>
/// <b>[JSON 이관 대비]</b> 하드코딩 데이터는 <see cref="CreateDefault"/> 정적 팩토리 메서드
/// 안에서만 만들어진다(정적 생성자가 아니므로 실패 시 예외가 그대로 전파된다). 나중에 JSON
/// 파일 기반으로 옮길 때는 <c>MonsterTable.FromJson(path)</c> 같은 별도 팩토리를 추가하기만
/// 하면 되고, <see cref="IMasterDataTable{TKey,T}"/>를 사용하는 다른 코드는 전혀 바뀔 필요가
/// 없다(코드리뷰 2026-07-06 H1 수정 — 이전에는 static class + 정적 필드 초기화식으로 고정되어
/// 있어 테스트에서 대체 데이터셋을 주입할 수 없었다).
/// <b>[ID 대역]</b> <see cref="MonsterTemplate.MonsterId"/>는 2000번대를 사용한다. 드롭 아이템
/// (<see cref="DropPool.ItemMetaId"/>)은 3000번대로, 장비(<c>GameServer.Items.EquipmentTemplate</c>)의
/// 4000~6000번대와 겹치지 않게 채번한다(코드리뷰 2026-07-06 Low 수정 — 이전에는 이 대역 규칙이
/// 문서화되지 않고 암묵적으로만 지켜지고 있었다). 7000번대는 공유 레이드 보스 전용이다(2026-07-07 추가).
/// <b>[조회 로직]</b> 코드리뷰 2026-07-06 Medium 수정: 조회는 이제
/// <see cref="MasterDataTable{TKey,T}"/> 공통 기반의 Dictionary 인덱스를 사용한다(과거 foreach
/// 선형 탐색이 3개 테이블에 중복돼 있던 것을 제거).
/// </remarks>
public sealed class MonsterTable : MasterDataTable<int, MonsterTemplate>
{
    /// <summary>주어진 템플릿 목록으로 테이블을 구성한다(몬스터 ID 중복 시 즉시 실패).</summary>
    /// <param name="templates">등록할 몬스터 템플릿 목록</param>
    /// <exception cref="ArgumentException"><paramref name="templates"/>에 <see cref="MonsterTemplate.MonsterId"/>
    /// 중복이 있는 경우</exception>
    public MonsterTable(IReadOnlyList<MonsterTemplate> templates)
        : base(templates, t => t.MonsterId, "몬스터 템플릿")
    {
    }

    /// <summary>하드코딩된 기본 10종 데이터로 테이블을 생성한다.</summary>
    public static MonsterTable CreateDefault() => new(BuildDefaultTemplates());

    private static List<MonsterTemplate> BuildDefaultTemplates() => new()
    {
        new()
        {
            MonsterId = 2001, Name = "슬라임", Level = 1, Hp = 20, Atk = 3, Def = 0,
            ExpDrop = 2, GoldDrop = 3,
            DropTable = [new DropPool { ItemMetaId = 3001, DropChance = 0.3f, MinQty = 1, MaxQty = 1 }]
        },
        new()
        {
            MonsterId = 2002, Name = "들쥐", Level = 2, Hp = 35, Atk = 5, Def = 1,
            ExpDrop = 4, GoldDrop = 5,
            DropTable = [new DropPool { ItemMetaId = 3002, DropChance = 0.25f, MinQty = 1, MaxQty = 1 }]
        },
        new()
        {
            MonsterId = 2003, Name = "고블린", Level = 3, Hp = 55, Atk = 8, Def = 2,
            ExpDrop = 6, GoldDrop = 8,
            DropTable = [new DropPool { ItemMetaId = 3003, DropChance = 0.25f, MinQty = 1, MaxQty = 2 }]
        },
        new()
        {
            MonsterId = 2004, Name = "늑대", Level = 4, Hp = 80, Atk = 12, Def = 3,
            AtkSpeed = 1.2, ExpDrop = 9, GoldDrop = 12,
            DropTable = [new DropPool { ItemMetaId = 3004, DropChance = 0.2f, MinQty = 1, MaxQty = 1 }]
        },
        new()
        {
            MonsterId = 2005, Name = "오크", Level = 5, Hp = 120, Atk = 18, Def = 6,
            ExpDrop = 13, GoldDrop = 18,
            DropTable = [new DropPool { ItemMetaId = 3005, DropChance = 0.2f, MinQty = 1, MaxQty = 2 }]
        },
        new()
        {
            MonsterId = 2006, Name = "스켈레톤", Level = 6, Hp = 160, Atk = 22, Def = 8,
            ExpDrop = 17, GoldDrop = 24,
            DropTable = [new DropPool { ItemMetaId = 3006, DropChance = 0.15f, MinQty = 1, MaxQty = 1 }],
            // 방어 관통 어픽스 — 방패 든 스켈레톤이 상대의 방어를 일부 무시한다는 컨셉.
            Affixes = [new StatModifier { StatType = StatType.ArmorPen, ModType = ModifierType.FlatAdd, Value = 5 }]
        },
        new()
        {
            MonsterId = 2007, Name = "오우거", Level = 7, Hp = 230, Atk = 30, Def = 12,
            ExpDrop = 24, GoldDrop = 32,
            DropTable = [new DropPool { ItemMetaId = 3007, DropChance = 0.15f, MinQty = 1, MaxQty = 2 }]
        },
        new()
        {
            MonsterId = 2008, Name = "다크엘프", Level = 8, Hp = 300, Atk = 40, Def = 15,
            ExpDrop = 30, GoldDrop = 42,
            DropTable = [new DropPool { ItemMetaId = 3008, DropChance = 0.1f, MinQty = 1, MaxQty = 1 }],
            // 치명타 확률 어픽스 — 궁수 계열이라는 컨셉.
            Affixes = [new StatModifier { StatType = StatType.CritProb, ModType = ModifierType.FlatAdd, Value = 0.15 }]
        },
        new()
        {
            MonsterId = 2009, Name = "트롤", Level = 9, Hp = 420, Atk = 55, Def = 20,
            Recovery = 5, ExpDrop = 40, GoldDrop = 55,
            DropTable = [new DropPool { ItemMetaId = 3009, DropChance = 0.1f, MinQty = 1, MaxQty = 2 }]
        },
        new()
        {
            MonsterId = 2010, Name = "리치", Level = 10, Hp = 600, Atk = 75, Def = 25,
            ExpDrop = 55, GoldDrop = 75,
            DropTable = [new DropPool { ItemMetaId = 3010, DropChance = 0.1f, MinQty = 1, MaxQty = 1 }],
            // 흡혈 어픽스 — 언데드 마법사가 피해량의 일부를 자기 체력으로 흡수한다는 컨셉.
            Affixes = [new StatModifier { StatType = StatType.Lifesteal, ModType = ModifierType.FlatAdd, Value = 0.1 }]
        },
        new()
        {
            MonsterId = 7001, Name = "레이드 보스", Level = 20,
            Hp = 5_000_000, Atk = 0, Def = 50,   // Atk=0: 반격 없음(레이드 스펙 확정). Def는 인코운터 내내 불변
            ExpDrop = 100_000, GoldDrop = 200_000,
            DropTable = []                        // 아이템 드롭 없음 — Exp/Gold만 기여 비례로 결정적 분배
        }
    };

}
