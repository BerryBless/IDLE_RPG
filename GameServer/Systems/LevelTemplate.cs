namespace GameServer.Systems;

/// <summary>
/// 플레이어 레벨 1개의 마스터 데이터 정의. 로직 없는 순수 데이터 프로퍼티만 갖는다.
/// </summary>
/// <remarks>
/// 모든 프로퍼티가 기본 타입으로만 구성되어 있어 <c>System.Text.Json</c>으로 그대로
/// (역)직렬화할 수 있다(<see cref="MonsterTemplate"/>/<see cref="Items.EquipmentTemplate"/>와
/// 동일한 JSON 이관 대비 설계).
/// </remarks>
public sealed class LevelTemplate
{
    /// <summary>레벨.</summary>
    public int Level { get; init; }

    /// <summary>이 레벨에 도달하기 위해 필요한 누적 경험치.</summary>
    public BigNumber RequiredExp { get; init; }

    /// <summary>이 레벨의 기본 체력.</summary>
    public BigNumber Hp { get; init; }

    /// <summary>이 레벨의 기본 공격력.</summary>
    public BigNumber Atk { get; init; }

    /// <summary>이 레벨의 기본 방어력.</summary>
    public BigNumber Def { get; init; }
}
