using GameServer.Entities;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

/// <summary>
/// F1 코드리뷰 발견 사항 회귀 테스트: 무기의 AttackScaling이 FinalStats에 자동 반영되어
/// CalcFinalDamage가 이를 온라인·오프라인 양쪽에서 동일하게 읽도록 한다.
/// </summary>
public class BattleManagerTests
{
    private static Monster MakeDummyTarget(double def = 0)
    {
        var monster = new Monster { InstanceId = "dummy", MonsterId = 1, Level = 1, BaseStats = new BaseStats { Def = def } };
        monster.UpdateFinalStats();
        return monster;
    }

    [Fact]
    public void CalcFinalDamage_NoWeaponEquipped_AttackScalingDefaultsToOne()
    {
        var player = new Player { InstanceId = "p1", AccountId = 1, Level = 1, BaseStats = new BaseStats { Atk = 100 } };
        player.UpdateFinalStats();
        var target = MakeDummyTarget();
        var battleManager = new BattleManager(new Random(0)); // internal ctor: 크리 없는 시드로 결정적 확인 목적이 아니라 접근만 필요

        Assert.Equal(1.0, player.FinalStats.AttackScaling, precision: 5);
    }

    [Fact]
    public void CalcFinalDamage_WeaponEquipped_AutomaticallyAppliesWeaponAttackScaling()
    {
        var player = new Player { InstanceId = "p1", AccountId = 1, Level = 1, BaseStats = new BaseStats { Atk = 100 } };
        var weapon = new Weapon { InstanceId = "w1", ItemMetaId = 1, Name = "테스트 검", AttackScaling = 2.0f };
        player.Equipment.Equip(weapon, SlotType.Weapon);
        player.UpdateFinalStats();

        Assert.Equal(2.0, player.FinalStats.AttackScaling, precision: 5);

        var target = MakeDummyTarget(def: 0);
        var battleManagerWithoutCrit = new BattleManager(new FixedRandom(0.999999)); // 크리 발생 안 하도록 고정

        var damage = battleManagerWithoutCrit.CalcFinalDamage(player, target);

        // Atk(100) * FinalStats.AttackScaling(2.0, 무기 자동 반영) * attackScaling 파라미터(기본 1.0) * defMult(1.0) = 200
        Assert.Equal(200, damage, precision: 5);
    }

    [Fact]
    public void CalcFinalDamage_ExtraAttackScalingParameter_MultipliesOnTopOfWeaponScaling()
    {
        var player = new Player { InstanceId = "p1", AccountId = 1, Level = 1, BaseStats = new BaseStats { Atk = 100 } };
        var weapon = new Weapon { InstanceId = "w1", ItemMetaId = 1, Name = "테스트 검", AttackScaling = 1.5f };
        player.Equipment.Equip(weapon, SlotType.Weapon);
        player.UpdateFinalStats();

        var target = MakeDummyTarget(def: 0);
        var battleManagerWithoutCrit = new BattleManager(new FixedRandom(0.999999));

        // 스킬 계수 2.0이 무기 배율(1.5) 위에 추가로 곱해짐: 100 * 1.5 * 2.0 * 1.0(defMult) = 300
        var damage = battleManagerWithoutCrit.CalcFinalDamage(player, target, attackScaling: 2.0f);

        Assert.Equal(300, damage, precision: 5);
    }
}
