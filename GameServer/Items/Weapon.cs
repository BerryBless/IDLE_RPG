namespace GameServer.Items;

/// <summary>근접/원거리 공격 도구가 되는 장비. 사거리 특성을 추가로 가진다.</summary>
public sealed class Weapon : Equipment
{
    /// <summary>공격 배율.</summary>
    public float AttackScaling { get; init; }
}
