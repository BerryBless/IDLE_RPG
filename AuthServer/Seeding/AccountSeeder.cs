using AuthServer.Accounts;
using AuthServer.Security;

namespace AuthServer.Seeding;

/// <summary>
/// 결정적(deterministic)인 더미 계정을 대량 생성하여 임의의 <see cref="IAccountRepository"/>에
/// 시딩합니다. 테스트에서는 인메모리 페이크에, 운영에서는 실제 MongoDB에 동일한 로직으로 재사용됩니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="SeedAsync"/>는 순차 실행을 가정합니다(내부 상태가 없어
/// 동시 호출 자체는 안전하지만, 같은 인덱스 범위로 중복 호출하면 저장소에 중복 upsert가 발생할 수
/// 있으므로 호출 측이 조율해야 합니다).
/// <b>[Memory Allocation:]</b> 계정 <paramref name="count"/>건을 한 번에 리스트로 구성한 뒤
/// <see cref="IAccountRepository.InsertManyAsync"/>로 배치 삽입합니다 — 대량 왕복 대신 단일 배치
/// I/O로 묶어 네트워크 라운드트립 수를 줄입니다.
/// <b>[Blocking:]</b> 각 계정의 비밀번호 해시 계산이 동기 CPU 블로킹이므로, count가 크고 해셔의
/// 반복 횟수가 높으면 전체 소요 시간이 선형으로 늘어납니다(테스트에서는 저비용 반복 횟수를 권장).
/// </remarks>
public static class AccountSeeder
{
    /// <summary>인덱스로부터 결정적인 사용자 이름을 생성합니다(예: <c>user0000</c>).</summary>
    /// <param name="index">0부터 시작하는 계정 인덱스입니다.</param>
    public static string UsernameFor(int index) => $"user{index:D4}";

    /// <summary>인덱스로부터 결정적인 평문 비밀번호를 생성합니다(예: <c>Pass!0000</c>).</summary>
    /// <param name="index">0부터 시작하는 계정 인덱스입니다.</param>
    public static string PasswordFor(int index) => $"Pass!{index:D4}";

    /// <summary>
    /// <paramref name="count"/>개의 결정적 더미 계정을 생성해 <paramref name="repository"/>에 시딩합니다.
    /// 인덱스 <c>i</c>는 <c>AccountId = i + 1</c>, <see cref="UsernameFor"/>/<see cref="PasswordFor"/>가
    /// 정의하는 자격증명으로 매핑됩니다 — 테스트가 동일 헬퍼로 기대 자격증명을 도출하는 단일 출처입니다.
    /// </summary>
    /// <param name="repository">시딩 대상 저장소입니다.</param>
    /// <param name="hasher">비밀번호 해시에 사용할 해셔입니다.</param>
    /// <param name="count">생성할 계정 수입니다. 기본값 3000.</param>
    /// <param name="ct">취소 토큰입니다.</param>
    public static async ValueTask SeedAsync(
        IAccountRepository repository, IPasswordHasher hasher, int count = 3000, CancellationToken ct = default)
    {
        var accounts = new List<Account>(count);
        for (int i = 0; i < count; i++)
        {
            accounts.Add(new Account
            {
                AccountId = i + 1,
                Username = UsernameFor(i),
                PasswordHash = hasher.Hash(PasswordFor(i)),
                CreatedAtUtc = DateTime.UtcNow,
            });
        }

        await repository.InsertManyAsync(accounts, ct);
    }
}
