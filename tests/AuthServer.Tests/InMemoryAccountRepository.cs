using System.Collections.Concurrent;
using AuthServer.Accounts;

namespace AuthServer.Tests;

/// <summary>
/// <see cref="IAccountRepository"/>의 테스트 전용 인메모리 페이크입니다. 실제 MongoDB 없이도
/// 계정 시딩·로그인 정확성 테스트를 빠르고 결정적으로 실행할 수 있게 해줍니다.
/// 운영 바이너리(AuthServer exe)에는 절대 포함하지 않습니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe.
/// <b>[Memory Allocation:]</b> 계정 전체를 프로세스 메모리에 보관합니다(3000건 규모의 테스트 용도로 적합).
/// <b>[Blocking:]</b> Non-blocking. 실제 I/O가 없으므로 <see cref="ValueTask"/>는 항상 완료된 상태로 반환됩니다.
/// </remarks>
public sealed class InMemoryAccountRepository : IAccountRepository
{
    // ConcurrentDictionary: lock-free 읽기/쓰기 경로를 제공해 여러 로그인 요청이 동시에
    // 조회해도 락 경합 없이 처리 가능. Username을 키로 사용해 FindByUsernameAsync를 O(1)로 지원.
    private readonly ConcurrentDictionary<string, Account> _accounts = new();

    /// <inheritdoc/>
    public ValueTask<Account?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        _accounts.TryGetValue(username, out Account? account);
        return ValueTask.FromResult(account);
    }

    /// <inheritdoc/>
    public ValueTask InsertAsync(Account account, CancellationToken ct = default)
    {
        _accounts[account.Username] = account;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask InsertManyAsync(IReadOnlyCollection<Account> accounts, CancellationToken ct = default)
    {
        foreach (Account account in accounts)
            _accounts[account.Username] = account;
        return ValueTask.CompletedTask;
    }
}
