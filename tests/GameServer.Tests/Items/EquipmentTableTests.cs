using GameServer.Items;

namespace GameServer.Tests.Items;

public class EquipmentTableTests
{
    [Fact]
    public void All_ContainsExactlyFifteenItems()
    {
        var table = EquipmentTable.CreateDefault();

        Assert.Equal(15, table.All.Count);
    }

    [Fact]
    public void All_ItemMetaIdsAreUnique()
    {
        var table = EquipmentTable.CreateDefault();
        var distinctIds = table.All.Select(e => e.ItemMetaId).Distinct().Count();

        Assert.Equal(table.All.Count, distinctIds);
    }

    [Fact]
    public void All_HasFiveOfEachSlotType()
    {
        var table = EquipmentTable.CreateDefault();

        Assert.Equal(5, table.All.Count(e => e.Slot == SlotType.Weapon));
        Assert.Equal(5, table.All.Count(e => e.Slot == SlotType.Armor));
        Assert.Equal(5, table.All.Count(e => e.Slot == SlotType.Accessory));
    }

    [Fact]
    public void GetById_KnownId_ReturnsMatchingTemplate()
    {
        var table = EquipmentTable.CreateDefault();
        var template = table.GetById(4001);

        Assert.Equal(4001, template.ItemMetaId);
        Assert.Equal("낡은 검", template.Name);
        Assert.Equal(SlotType.Weapon, template.Slot);
    }

    [Fact]
    public void GetById_UnknownId_ThrowsKeyNotFoundException()
    {
        var table = EquipmentTable.CreateDefault();

        Assert.Throws<KeyNotFoundException>(() => table.GetById(999999));
    }

    [Fact]
    public void Constructor_AcceptsCustomTemplateList_IndependentOfDefaultInstance()
    {
        // 코드리뷰 H1: static 고정을 인터페이스+인스턴스 기반으로 바꾼 목적을 직접 검증.
        var custom = new EquipmentTable(new List<EquipmentTemplate>
        {
            new() { ItemMetaId = 9001, Name = "테스트 장비", Slot = SlotType.Weapon }
        });
        var defaultTable = EquipmentTable.CreateDefault();

        Assert.Single(custom.All);
        Assert.Equal(15, defaultTable.All.Count);
        Assert.Throws<KeyNotFoundException>(() => defaultTable.GetById(9001));
    }
}
