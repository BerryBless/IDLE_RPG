using MongoDB.Bson.Serialization.Attributes;

namespace AuthServer.Accounts;

/// <summary>
/// 인증 계정 도메인 모델입니다. MongoDB의 <c>accounts</c> 컬렉션 문서에 매핑됩니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not Thread-safe. 각 인스턴스는 단일 요청/시딩 컨텍스트에서만 다뤄야 합니다
/// (모든 속성이 <c>init</c>이라 생성 후 값이 바뀌지 않으므로 읽기 전용 공유는 안전).
/// <b>[Memory Allocation:]</b> 일반 POCO입니다. BSON (역)직렬화 시 MongoDB.Driver가 필드별로
/// 힙 할당을 수행합니다.
/// </remarks>
public sealed class Account
{
    /// <summary>계정 고유 식별자입니다. MongoDB 문서의 <c>_id</c>로 매핑됩니다.</summary>
    [BsonId]
    public int AccountId { get; init; }

    /// <summary>로그인에 사용하는 사용자 이름입니다. 컬렉션에 고유 인덱스가 걸려 있어야 합니다.</summary>
    [BsonElement("username")]
    public string Username { get; init; } = string.Empty;

    /// <summary>PBKDF2로 해시된 비밀번호 인코딩 문자열입니다. 평문 비밀번호는 저장하지 않습니다.</summary>
    [BsonElement("passwordHash")]
    public string PasswordHash { get; init; } = string.Empty;

    /// <summary>계정 생성 시각(UTC)입니다.</summary>
    [BsonElement("createdAtUtc")]
    public DateTime CreatedAtUtc { get; init; }
}
