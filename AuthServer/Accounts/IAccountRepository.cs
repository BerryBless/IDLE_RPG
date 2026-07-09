namespace AuthServer.Accounts;

/// <summary>
/// 계정 저장소에 대한 조회·등록 계약입니다. 운영에서는 MongoDB(<see cref="MongoAccountRepository"/>),
/// 테스트에서는 인메모리 페이크로 구현되어 로그인 서비스와 계정 시더가 저장소 종류에 관계없이
/// 동일하게 동작할 수 있습니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현체는 Thread-safe해야 합니다. 여러 로그인 요청이
/// 동시에 같은 저장소 인스턴스로 조회할 수 있습니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 조회 결과로 반환되는 <see cref="Account"/>는 매번
/// 새로 생성된 힙 객체입니다(호출자가 자유롭게 보유·수정 가능).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking, 비동기 I/O. MongoDB 구현체는 실제 네트워크
/// 왕복이 발생하므로 IO 스레드에서 동기적으로 기다리면 안 됩니다.</description></item>
/// </list>
/// </remarks>
public interface IAccountRepository
{
    /// <summary>사용자 이름으로 계정을 조회합니다.</summary>
    /// <param name="username">조회할 사용자 이름입니다.</param>
    /// <param name="ct">취소 토큰입니다.</param>
    /// <returns>일치하는 계정입니다. 없으면 <c>null</c>입니다.</returns>
    ValueTask<Account?> FindByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>계정 1건을 저장소에 추가합니다.</summary>
    /// <param name="account">추가할 계정입니다.</param>
    /// <param name="ct">취소 토큰입니다.</param>
    ValueTask InsertAsync(Account account, CancellationToken ct = default);

    /// <summary>여러 계정을 배치로 추가합니다(대량 시딩용).</summary>
    /// <param name="accounts">추가할 계정 목록입니다.</param>
    /// <param name="ct">취소 토큰입니다.</param>
    ValueTask InsertManyAsync(IReadOnlyCollection<Account> accounts, CancellationToken ct = default);
}
