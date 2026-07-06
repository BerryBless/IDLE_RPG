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

    [Fact]
    public void Constructor_DuplicateItemMetaIds_ThrowsArgumentException()
    {
        // 코드리뷰 2026-07-06 Medium 수정: Dictionary 인덱스 구축이 중복 ID를 테이블 생성 시점에
        // 즉시 걸러낸다(마스터 데이터 설정 오류를 조용히 넘기지 않음).
        Assert.Throws<ArgumentException>(() => new EquipmentTable(new List<EquipmentTemplate>
        {
            new() { ItemMetaId = 1, Name = "A", Slot = SlotType.Weapon },
            new() { ItemMetaId = 1, Name = "B", Slot = SlotType.Armor }
        }));
    }
}
