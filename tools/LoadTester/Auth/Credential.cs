namespace LoadTester.Auth;

/// <summary>가상 클라이언트 1개가 사용할 시딩 계정 자격 증명입니다.</summary>
/// <param name="AccountIndex">시딩 계정 인덱스(0..Accounts-1). HMAC 토큰의 accountId로도 쓰인다.</param>
/// <param name="Username">계정명(AuthServer 시딩 규칙 <c>user{i:D4}</c>).</param>
/// <param name="Password">비밀번호(AuthServer 시딩 규칙 <c>Pass!{i:D4}</c>).</param>
public readonly record struct Credential(int AccountIndex, string Username, string Password);
