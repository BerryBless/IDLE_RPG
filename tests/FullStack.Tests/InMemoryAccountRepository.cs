using System.Collections.Concurrent;
using AuthServer.Accounts;

namespace FullStack.Tests;

/// <summary>
/// <see cref="IAccountRepository"/>의 테스트 전용 인메모리 페이크입니다(AuthServer.Tests의 동일 페이크를
/// 복사 — 테스트 프로젝트 간 클래스 공유가 어려워 재정의). 실제 MongoDB 없이 로그인 경로를 검증합니다.
/// </summary>
/// <remarks><b>[Thread Safety:]</b> Thread-safe. <b>[Blocking:]</b> Non-blocking(실 I/O 없음).</remarks>
public sealed class InMemoryAccountRepository : IAccountRepository
{
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
