// 픽스처: code-review 하네스(architecture/security/performance/style 4개 도메인)가 탐지해야 할
// 9개 결함을 심어놓은 소스. tests/fixtures/ 아래(모든 .csproj 폴더 밖)에 있어 컴파일되지 않는다.

using System;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Collections.Generic;

namespace Fixtures.CodeReview;

// CR4: SRP 위반 — 데이터 접근 + 비즈니스 로직 + 포맷팅까지 한 클래스가 담당하는 갓 클래스
public sealed class OrderService
{
    // CR2: CWE-798 — 하드코딩된 API 키
    private const string ApiKey = "sk-live-abc123def456ghi789";

    // CR5: DIP 위반 — 추상화(인터페이스) 없이 구체 구현을 직접 new
    private readonly SqlUserRepository _repo = new SqlUserRepository();

    // CR1: CWE-89 — 사용자 입력을 SQL 문자열에 직접 연결 (SQL 인젝션)
    public List<string> FindUserByName(string name)
    {
        var query = "SELECT * FROM Users WHERE Name='" + name + "'"; // CR1: SQL injection
        using var cmd = new SqlCommand(query);
        return new List<string>();
    }

    // CR3: 약한 암호화 — 비밀번호 해싱에 MD5 단독 사용
    public string HashPassword(string password)
    {
        using var md5 = MD5.Create(); // CR3: weak crypto (MD5)
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        return Convert.ToBase64String(md5.ComputeHash(bytes));
    }

    // CR6: N+1 쿼리 — 루프 안에서 매 반복마다 개별 DB 조회
    public void PrintCustomerNames(List<Order> orders)
    {
        foreach (var order in orders)
        {
            var customer = _repo.Find(order.CustomerId); // CR6: N+1 query
            Console.WriteLine(customer);
        }
    }

    // CR7: 동기 블로킹 — async 결과를 .Result로 강제 대기
    public string GetUserAsyncBlocking(int id)
    {
        return _repo.FindAsync(id).Result; // CR7: blocking on async
    }

    // CR8: 네이밍 컨벤션 위반 — public 필드가 PascalCase가 아니고 메서드가 camelCase가 아님
    public int userName;
    public string getuser() => userName.ToString(); // CR8: naming convention violation

    // CR9: 빈 catch 블록 — 예외를 삼켜버려 오류 원인 추적 불가
    public void RiskyOperation()
    {
        try
        {
            DoSomething();
        }
        catch (Exception) // CR9: empty catch block
        {
        }
    }

    private void DoSomething() { }
}

public sealed class SqlUserRepository
{
    public string Find(int id) => "";
    public System.Threading.Tasks.Task<string> FindAsync(int id) => System.Threading.Tasks.Task.FromResult("");
}

public sealed class Order
{
    public int CustomerId { get; set; }
}
