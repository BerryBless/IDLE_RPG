using GameServer.Items;
using GameServer.Stats;

namespace GameServer.Tests.Items;

public class EquipmentFactoryTests
{
    // 인스턴스이지만 데이터가 불변(생성자에서만 설정)이라 여러 테스트가 동시에 읽어도 안전하다.
    private static readonly EquipmentTable Table = EquipmentTable.CreateDefault();

    [Fact]
    public void Create_WeaponTemplate_ReturnsWeaponWithAttackScalingAndModifiers()
    {
        var template = Table.GetById(4001); // 낡은 검: AttackScaling 1.5, Atk+1

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
        var template = Table.GetById(5001); // 가죽 갑옷: Def+3

        var equipment = EquipmentFactory.Create(template);

        var armor = Assert.IsType<Armor>(equipment);
        Assert.Contains(armor.BaseModifiers, m => m.StatType == StatType.Def && m.Value == 3);
    }

    [Fact]
    public void Create_AccessoryTemplate_ReturnsAccessory()
    {
        var template = Table.GetById(6001); // 낡은 반지: CritProb+0.02

        var equipment = EquipmentFactory.Create(template);

        var accessory = Assert.IsType<Accessory>(equipment);
        Assert.Contains(accessory.BaseModifiers, m => m.StatType == StatType.CritProb);
    }

    [Fact]
    public void Create_EachCallProducesIndependentInstance()
    {
        var template = Table.GetById(4001);

        var first = EquipmentFactory.Create(template);
        var second = EquipmentFactory.Create(template);

        Assert.NotSame(first, second);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
    }

    [Fact]
    public void Create_MutatingTemplateAfterCreate_DoesNotAffectCreatedInstance()
    {
        // BaseModifiers는 템플릿 리스트를 복사해 전달해야 한다(인스턴스 간 리스트 공유 방지).
        // 공유 Table 인스턴스를 오염시키지 않도록 이 템플릿만 별도로 복제해 사용한다.
        var template = new EquipmentTemplate
        {
            ItemMetaId = 5001,
            Name = "가죽 갑옷(복제)",
            Slot = SlotType.Armor,
            BaseModifiers = [.. Table.GetById(5001).BaseModifiers]
        };
        var originalCount = template.BaseModifiers.Count;

        var equipment = EquipmentFactory.Create(template);
        template.BaseModifiers.Add(new StatModifier { StatType = StatType.Hp, ModType = ModifierType.FlatAdd, Value = 999 });

        Assert.Equal(originalCount, equipment.BaseModifiers.Count);
    }
}
