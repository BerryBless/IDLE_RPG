using MongoDB.Driver;

namespace AuthServer.Accounts;

/// <summary>
/// MongoDB 컬렉션을 백엔드로 사용하는 <see cref="IAccountRepository"/> 운영 구현체입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="IMongoCollection{TDocument}"/>는
/// MongoDB.Driver 내부적으로 커넥션 풀 위의 얇은 래퍼로 동작하도록 설계되어 있어 여러 스레드가
/// 동시에 같은 인스턴스로 조회/삽입해도 안전합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 조회/삽입마다 BSON (역)직렬화로 인한 힙 할당이
/// 발생합니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 모든 메서드가 비동기 드라이버 API
/// (<c>FindAsync</c>/<c>InsertOneAsync</c>/<c>InsertManyAsync</c>)를 그대로 위임하므로 네트워크
/// 왕복 동안 호출 스레드를 점유하지 않습니다.</description></item>
/// </list>
/// </remarks>
public sealed class MongoAccountRepository : IAccountRepository
{
    private const string CollectionName = "accounts";

    // IMongoCollection<T>: MongoDB.Driver 내부에서 커넥션 풀 위의 얇은 래퍼로 동작 —
    // 인스턴스 자체가 스레드 안전하며 매 호출 새 연결을 열지 않고 풀에서 대여/반환한다.
    private readonly IMongoCollection<Account> _collection;

    /// <summary>연결 문자열과 데이터베이스 이름으로 저장소를 생성합니다.</summary>
    /// <param name="connectionString">MongoDB 연결 문자열입니다.</param>
    /// <param name="databaseName">사용할 데이터베이스 이름입니다.</param>
    public MongoAccountRepository(string connectionString, string databaseName)
    {
        var client = new MongoClient(connectionString);
        IMongoDatabase database = client.GetDatabase(databaseName);
        _collection = database.GetCollection<Account>(CollectionName);
    }

    /// <summary>
    /// <see cref="Account.Username"/>에 고유 인덱스를 보장합니다. 이미 존재하면 아무 동작도
    /// 하지 않는 멱등 연산이라 앱 시작 시마다 안전하게 호출할 수 있습니다.
    /// </summary>
    /// <param name="ct">취소 토큰입니다.</param>
    public async ValueTask EnsureIndexesAsync(CancellationToken ct = default)
    {
        var keys = Builders<Account>.IndexKeys.Ascending(a => a.Username);
        var model = new CreateIndexModel<Account>(keys, new CreateIndexOptions { Unique = true });
        await _collection.Indexes.CreateOneAsync(model, cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async ValueTask<Account?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        using IAsyncCursor<Account> cursor =
            await _collection.FindAsync(a => a.Username == username, cancellationToken: ct);
        return await cursor.FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public async ValueTask InsertAsync(Account account, CancellationToken ct = default)
    {
        await _collection.InsertOneAsync(account, cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async ValueTask InsertManyAsync(IReadOnlyCollection<Account> accounts, CancellationToken ct = default)
    {
        if (accounts.Count == 0)
            return;
        await _collection.InsertManyAsync(accounts, cancellationToken: ct);
    }

    /// <summary>
    /// <c>accounts</c> 컬렉션의 모든 문서를 삭제합니다. <c>AuthServer --seed --force</c>가 재시딩 전
    /// 기존 더미 계정을 정리하는 용도로만 사용합니다. <see cref="IAccountRepository"/> 계약에는
    /// 포함하지 않습니다 — 파괴적 연산을 일반 로그인 경로(테스트 페이크 포함)에 노출하지 않기 위함입니다.
    /// </summary>
    /// <param name="ct">취소 토큰입니다.</param>
    public async ValueTask DropAllAsync(CancellationToken ct = default)
    {
        await _collection.DeleteManyAsync(FilterDefinition<Account>.Empty, ct);
    }
}
