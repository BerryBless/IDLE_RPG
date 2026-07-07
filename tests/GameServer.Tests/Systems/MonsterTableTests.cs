using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class MonsterTableTests
{
    [Fact]
    public void All_ContainsExactlyElevenMonsters()
    {
        var table = MonsterTable.CreateDefault();

        Assert.Equal(11, table.All.Count);
    }

    [Fact]
    public void All_MonsterIdsAreUnique()
    {
        var table = MonsterTable.CreateDefault();
        var distinctIds = table.All.Select(m => m.MonsterId).Distinct().Count();

        Assert.Equal(table.All.Count, distinctIds);
    }

    [Fact]
    public void GetById_KnownId_ReturnsMatchingTemplate()
    {
        var table = MonsterTable.CreateDefault();
        var template = table.GetById(2001);

        Assert.Equal(2001, template.MonsterId);
        Assert.Equal("슬라임", template.Name);
    }

    [Fact]
    public void GetById_UnknownId_ThrowsKeyNotFoundException()
    {
        var table = MonsterTable.CreateDefault();

        Assert.Throws<KeyNotFoundException>(() => table.GetById(999999));
    }

    [Fact]
    public void Constructor_AcceptsCustomTemplateList_IndependentOfDefaultInstance()
    {
        // 코드리뷰 H1: static 고정을 인터페이스+인스턴스 기반으로 바꾼 목적을 직접 검증 —
        // 커스텀 데이터로 만든 인스턴스가 기본 인스턴스와 서로 영향을 주지 않아야 한다.
        var custom = new MonsterTable(new List<MonsterTemplate>
        {
            new() { MonsterId = 9001, Name = "테스트 몬스터", Level = 1, Hp = 1, Atk = 1, Def = 0 }
        });
        var defaultTable = MonsterTable.CreateDefault();

        Assert.Single(custom.All);
        Assert.Equal(9001, custom.GetById(9001).MonsterId);
        Assert.Equal(11, defaultTable.All.Count); // 커스텀 인스턴스가 기본 데이터에 영향 없음
        Assert.Throws<KeyNotFoundException>(() => defaultTable.GetById(9001));
    }

    [Fact]
    public void Constructor_DuplicateMonsterIds_ThrowsArgumentException()
    {
        // 코드리뷰 2026-07-06 Medium 수정: Dictionary 인덱스 구축이 중복 ID를 테이블 생성 시점에
        // 즉시 걸러낸다(마스터 데이터 설정 오류를 조용히 넘기지 않음).
        Assert.Throws<ArgumentException>(() => new MonsterTable(new List<MonsterTemplate>
        {
            new() { MonsterId = 1, Name = "A", Level = 1, Hp = 1, Atk = 1, Def = 0 },
            new() { MonsterId = 1, Name = "B", Level = 1, Hp = 1, Atk = 1, Def = 0 }
        }));
    }

    [Fact]
    public void GetById_RaidBossId_ReturnsMatchingTemplate()
    {
        var table = MonsterTable.CreateDefault();
        var template = table.GetById(7001);

        Assert.Equal(7001, template.MonsterId);
        Assert.Equal(0, template.Atk); // 레이드 보스는 반격하지 않는다(스펙 확정 사항)
    }
}
