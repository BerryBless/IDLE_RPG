using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// 몬스터 1종(種)의 마스터 데이터 정의. 로직 없는 순수 데이터 프로퍼티만 갖는다.
/// </summary>
/// <remarks>
/// 모든 프로퍼티가 기본 타입·단순 리스트(<see cref="DropPool"/>/<see cref="StatModifier"/>도 로직 없는
/// 순수 데이터 클래스)로만 구성되어 있어 <c>System.Text.Json</c>으로 그대로 (역)직렬화할 수 있다.
/// 현재는 <see cref="MonsterTable"/>에 C# 하드코딩 리스트로 존재하지만, 나중에 JSON 파일 기반
/// 마스터 데이터로 옮길 때 이 타입 자체는 바꿀 필요가 없다(로딩 방식만 <see cref="MonsterTable"/>에서 교체).
/// </remarks>
public sealed class MonsterTemplate
{
    /// <summary>몬스터 정의 테이블(마스터 데이터) ID. <see cref="Entities.Monster.MonsterId"/>에 그대로 대응.</summary>
    public int MonsterId { get; init; }

    /// <summary>몬스터 이름(표시용).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>몬스터 레벨.</summary>
    public int Level { get; init; }

    /// <summary>기본 체력.</summary>
    public BigNumber Hp { get; init; }

    /// <summary>기본 공격력.</summary>
    public BigNumber Atk { get; init; }

    /// <summary>기본 방어력.</summary>
    public BigNumber Def { get; init; }

    /// <summary>초당 자연 회복량.</summary>
    public BigNumber Recovery { get; init; }

    /// <summary>공격 속도.</summary>
    public double AtkSpeed { get; init; }

    /// <summary>치명타 확률.</summary>
    public double CritProb { get; init; }

    /// <summary>치명타 피해량 배율.</summary>
    public double CritDmg { get; init; }

    /// <summary>처치 시 지급되는 경험치.</summary>
    public BigNumber ExpDrop { get; init; }

    /// <summary>처치 시 지급되는 골드.</summary>
    public BigNumber GoldDrop { get; init; }

    /// <summary>아이템 드롭 확률표.</summary>
    public List<DropPool> DropTable { get; init; } = new();

    /// <summary>이 몬스터 종 고유의 어픽스(특수 스탯 수정치).</summary>
    public List<StatModifier> Affixes { get; init; } = new();
}
