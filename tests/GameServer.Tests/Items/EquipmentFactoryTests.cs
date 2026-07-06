using GameServer.Items;
using GameServer.Stats;

namespace GameServer.Tests.Items;

public class EquipmentFactoryTests
{
    [Fact]
    public void Create_WeaponTemplate_ReturnsWeaponWithAttackScalingAndModifiers()
    {
        var template = EquipmentTable.GetById(4001); // 낡은 검: AttackScaling 1.5, Atk+1

        var equipment = EquipmentFactory.Create(template);

        var weapon = Assert.IsType<Weapon>(equipment);
        Assert.Equal(4001, weapon.ItemMetaId);
        Assert.Equal("낡은 검", weapon.Name);
        Assert.Equal(1.5f, weapon.AttackScaling);
        Assert.Contains(weapon.BaseModifiers, m => m.StatType == StatType.Atk && m.Value == 1);
    }

    [Fact]
    public void Create_ArmorTemplate_ReturnsArmor()
    {
        var template = EquipmentTable.GetById(5001); // 가죽 갑옷: Def+3

        var equipment = EquipmentFactory.Create(template);

        var armor = Assert.IsType<Armor>(equipment);
        Assert.Contains(armor.BaseModifiers, m => m.StatType == StatType.Def && m.Value == 3);
    }

    [Fact]
    public void Create_AccessoryTemplate_ReturnsAccessory()
    {
        var template = EquipmentTable.GetById(6001); // 낡은 반지: CritProb+0.02

        var equipment = EquipmentFactory.Create(template);

        var accessory = Assert.IsType<Accessory>(equipment);
        Assert.Contains(accessory.BaseModifiers, m => m.StatType == StatType.CritProb);
    }

    [Fact]
    public void Create_EachCallProducesIndependentInstance()
    {
        var template = EquipmentTable.GetById(4001);

        var first = EquipmentFactory.Create(template);
        var second = EquipmentFactory.Create(template);

        Assert.NotSame(first, second);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
    }

    [Fact]
    public void Create_MutatingTemplateAfterCreate_DoesNotAffectCreatedInstance()
    {
        // BaseModifiers는 템플릿 리스트를 복사해 전달해야 한다(인스턴스 간 리스트 공유 방지).
        var template = EquipmentTable.GetById(5001);
        var originalCount = template.BaseModifiers.Count;

        var equipment = EquipmentFactory.Create(template);
        template.BaseModifiers.Add(new StatModifier { StatType = StatType.Hp, ModType = ModifierType.FlatAdd, Value = 999 });

        Assert.Equal(originalCount, equipment.BaseModifiers.Count);
    }
}
