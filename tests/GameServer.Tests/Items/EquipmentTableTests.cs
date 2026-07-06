using GameServer.Items;

namespace GameServer.Tests.Items;

public class EquipmentTableTests
{
    [Fact]
    public void All_ContainsExactlyFifteenItems()
    {
        Assert.Equal(15, EquipmentTable.All.Count);
    }

    [Fact]
    public void All_ItemMetaIdsAreUnique()
    {
        var distinctIds = EquipmentTable.All.Select(e => e.ItemMetaId).Distinct().Count();

        Assert.Equal(EquipmentTable.All.Count, distinctIds);
    }

    [Fact]
    public void All_HasFiveOfEachSlotType()
    {
        Assert.Equal(5, EquipmentTable.All.Count(e => e.Slot == SlotType.Weapon));
        Assert.Equal(5, EquipmentTable.All.Count(e => e.Slot == SlotType.Armor));
        Assert.Equal(5, EquipmentTable.All.Count(e => e.Slot == SlotType.Accessory));
    }

    [Fact]
    public void GetById_KnownId_ReturnsMatchingTemplate()
    {
        var template = EquipmentTable.GetById(4001);

        Assert.Equal(4001, template.ItemMetaId);
        Assert.Equal("낡은 검", template.Name);
        Assert.Equal(SlotType.Weapon, template.Slot);
    }

    [Fact]
    public void GetById_UnknownId_ThrowsKeyNotFoundException()
    {
        Assert.Throws<KeyNotFoundException>(() => EquipmentTable.GetById(999999));
    }
}
